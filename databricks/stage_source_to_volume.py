# Databricks notebook source
# MAGIC %md
# MAGIC # Stage the source tables to a Unity Catalog volume
# MAGIC
# MAGIC Reads the four source tables out of the source Azure SQL database and writes them as Parquet into the
# MAGIC migration volume. The .NET worker (`MixMix.MigrationWorker`) then reads those files and merges them into
# MAGIC the target database.
# MAGIC
# MAGIC Fill in the `<...>` widget defaults below with your own server, database and volume.
# MAGIC
# MAGIC Only the columns the target actually needs are selected. `dbo.ItemNew` in particular holds
# MAGIC `varbinary(max)` image columns that the target `dbo.Item` has no place for, and pulling them across
# MAGIC would dominate the extract for no benefit.
# MAGIC
# MAGIC Store the source password in a secret scope rather than in the notebook:
# MAGIC
# MAGIC ```
# MAGIC databricks secrets create-scope <scope-name>
# MAGIC databricks secrets put-secret <scope-name> sql-password
# MAGIC ```

# COMMAND ----------

from datetime import datetime, timezone

dbutils.widgets.text("run_id", f"run_{datetime.now(timezone.utc):%Y%m%d}_001", "Run id")
dbutils.widgets.text("secret_scope", "<scope-name>", "Secret scope holding sql-password")
dbutils.widgets.text("stage_root", "/Volumes/<catalog>/<schema>/<volume>", "Volume folder for run folders")
dbutils.widgets.text("sql_server", "<sql-server>.database.windows.net", "Source SQL server")
dbutils.widgets.text("sql_database", "<source-database>", "Source database")
dbutils.widgets.text("sql_user", "<sql-user>", "Source SQL login")

RUN_ID = dbutils.widgets.get("run_id")
SECRET_SCOPE = dbutils.widgets.get("secret_scope")

BASE_PATH = f"{dbutils.widgets.get('stage_root').rstrip('/')}/{RUN_ID}"

ITEM_CATEGORY_PATH = f"{BASE_PATH}/itemcategory"
BLOB_PATH = f"{BASE_PATH}/blob"
ITEM_PATH = f"{BASE_PATH}/item"
ITEM_BLOB_PATH = f"{BASE_PATH}/itemblob"

SQL_SERVER = dbutils.widgets.get("sql_server")
SQL_PORT = 1433
SQL_DATABASE = dbutils.widgets.get("sql_database")
SQL_USER = dbutils.widgets.get("sql_user")
# The password is never inlined here; it is read from a Databricks secret scope.
SQL_PASSWORD = dbutils.secrets.get(scope=SECRET_SCOPE, key="sql-password")

if "<" in f"{BASE_PATH}{SQL_SERVER}{SQL_DATABASE}{SQL_USER}":
    raise ValueError("Fill in the notebook widgets: they still hold <placeholder> values.")

JDBC_URL = (
    f"jdbc:sqlserver://{SQL_SERVER}:{SQL_PORT};"
    f"database={SQL_DATABASE};"
    "encrypt=true;trustServerCertificate=false;loginTimeout=60;"
)

print(f"Staging run {RUN_ID} to {BASE_PATH}")

# COMMAND ----------


def read_source(query: str):
    """Reads a source query over JDBC. The query is passed as a derived table, so it must be parenthesised."""
    return (
        spark.read.format("jdbc")
        .option("url", JDBC_URL)
        .option("dbtable", query)
        .option("user", SQL_USER)
        .option("password", SQL_PASSWORD)
        .option("driver", "com.microsoft.sqlserver.jdbc.SQLServerDriver")
        .load()
    )


def stage(query: str, path: str, label: str) -> int:
    df = read_source(query)
    # Coalescing keeps the worker's file count low; these tables are small enough for a single file each.
    df.coalesce(1).write.mode("overwrite").parquet(path)
    count = spark.read.parquet(path).count()
    print(f"{label}: staged {count:,} row(s) to {path}")
    return count


# COMMAND ----------

# MAGIC %md
# MAGIC ## dbo.Category -> itemcategory
# MAGIC Becomes the target `dbo.ItemCategory`, the parent of `dbo.Item.CategoryId`.

# COMMAND ----------

stage("(SELECT Id, Name FROM dbo.Category) AS c", ITEM_CATEGORY_PATH, "ItemCategory")

# COMMAND ----------

# MAGIC %md
# MAGIC ## dbo.Blob -> blob

# COMMAND ----------

stage("(SELECT BlobId, Name FROM dbo.Blob) AS b", BLOB_PATH, "Blob")

# COMMAND ----------

# MAGIC %md
# MAGIC ## dbo.ItemNew -> item
# MAGIC `Deleted` and `Active` are carried across so the worker can filter on them; everything else the
# MAGIC target does not model is left behind.

# COMMAND ----------

stage(
    "(SELECT ItemID, CategoryId, Name, Deleted, Active FROM dbo.ItemNew) AS i",
    ITEM_PATH,
    "Item",
)

# COMMAND ----------

# MAGIC %md
# MAGIC ## dbo.ItemBlob -> itemblob

# COMMAND ----------

stage("(SELECT ItemBlobId, ItemId, BlobId FROM dbo.ItemBlob) AS ib", ITEM_BLOB_PATH, "ItemBlob")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Done
# MAGIC Point the worker at this run with `Migration:RunId`, or leave it empty to pick up the newest run folder.

# COMMAND ----------

print(f"Run {RUN_ID} staged. Set Migration:RunId={RUN_ID} for the .NET worker.")
dbutils.notebook.exit(RUN_ID)
