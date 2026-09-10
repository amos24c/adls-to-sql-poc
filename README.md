# ADLS to Azure SQL sync worker

A .NET 10 background worker that reads staged Parquet extracts out of a Databricks Unity Catalog volume and
merges them into an Azure SQL database.

```
source database (Azure SQL)
        │  databricks/stage_source_to_volume.py   (Spark job, JDBC read → Parquet write)
        ▼
/Volumes/<catalog>/<schema>/<volume>/<run-id>/{itemcategory,blob,item,itemblob}
        │  src/MixMix.MigrationWorker              (Databricks Files API → SqlBulkCopy → MERGE)
        ▼
target database (Azure SQL): dbo.ItemCategory, dbo.Blob, dbo.Item, dbo.ItemBlob
```

Every environment-specific value in this repo is a `<placeholder>`. Nothing here carries a real host, database,
login, token or volume path; see [Configure the worker](#3-configure-the-worker).

## How a pass works

For each table, in this order — `ItemCategory`, `Blob`, `Item`, `ItemBlob` — the worker:

1. Lists the staged folder through the Databricks Files API (`GET /api/2.0/fs/directories{path}`), recursing
   into subfolders and ignoring Spark bookkeeping entries such as `_SUCCESS` and `.crc` files.
2. Downloads each Parquet file (`GET /api/2.0/fs/files{path}`) to a temp file, because Parquet needs random
   access and the API only returns a forward-only stream.
3. Streams the file one row group at a time, decoding **only the columns the target needs**.
4. Maps and buffers rows, then `SqlBulkCopy`s them into a session-scoped `#stage_<Table>` temp table.
5. Runs a single idempotent `MERGE` from the staging table into the target.

The order is the foreign-key order: `dbo.Item` needs its category to exist, and `dbo.ItemBlob` needs both its
item and its blob.

## Mapping decisions

| Target | Source | Notes |
| --- | --- | --- |
| `ItemCategory.Id` | `Category.Id` | Carried across verbatim under `IDENTITY_INSERT`. |
| `ItemCategory.Descr` | `Category.Name` | The column is renamed; both are 100 characters. |
| `Blob.BlobId`, `.Name` | `Blob.BlobId`, `.Name` | Like for like. |
| `Item.Id` | `ItemNew.ItemID` | Carried across verbatim under `IDENTITY_INSERT`. |
| `Item.CategoryId` | `ItemNew.CategoryId` | Filtered to categories that exist. |
| `Item.Name` | `ItemNew.Name` | Narrows from `varchar(200)` to `nvarchar(100)`. |
| `ItemBlob.*` | `ItemBlob.*` | Like for like. |

Three of these deserve explanation:

- **Source ids are preserved.** `dbo.Item.Id` and `dbo.ItemCategory.Id` are both `IDENTITY` columns, but
  `dbo.ItemBlob.ItemId` and `dbo.Item.CategoryId` reference them. Letting the target generate fresh identity
  values would break those relationships, so those loads run under `SET IDENTITY_INSERT` and then call
  `DBCC CHECKIDENT ... RESEED` so the next application insert does not collide. This needs `ALTER` on both tables.
- **`Item.Name` is truncated.** The target column is half the width of the source. Values longer than 100
  characters are shortened and the count is logged as a warning and recorded in `RowsFiltered`/run history.
- **Orphans and duplicates are dropped, not failed on.** Each `MERGE` de-duplicates on the primary key and
  filters to rows whose parents exist, and reports how many it discarded as `Rejected`. Without this, one bad
  row would fail an entire batch on a foreign-key violation.

### Deletes are opt-in

The merges only insert and update, so a row that disappears from the source stays in the target. Turning on
`Migration:PruneMissingRows` makes the `dbo.Item` and `dbo.ItemBlob` steps delete target rows the extract no
longer contains. It is off by default because an extract that ran against a partly-populated source would then
delete good target rows. Two guards apply: pruning is skipped when the extract staged no rows at all, and
`dbo.Item`'s prune removes dependent `dbo.ItemBlob` rows first so the foreign key holds. Pruning `dbo.Blob` or
`dbo.ItemCategory` would cascade into their children and is not implemented.

`ItemNew.Deleted` rows are excluded by default (`Migration:ExcludeDeletedItems`); `Active` is available as an
optional filter but off by default. Categories, by contrast, are loaded regardless of their `Active`/`Deleted`
flags, because dropping one would orphan every item pointing at it.

## Setup

### 1. Target database

The four target tables are expected to already exist. Run `sql/01_target_prerequisites.sql` against the target
database to create the `dbo.MigrationTableRun` bookkeeping table (which the worker also creates itself by
default) and to see the grants the worker needs.

### 1a. If your extract has no `itemcategory` folder

`dbo.Item.CategoryId` has a foreign key to `dbo.ItemCategory`, so without categories every item row is
rejected as an orphan. If the export job does not stage an `itemcategory` folder, load that one table straight
from the source with `sql/03_seed_itemcategory.sql`, and add `"ItemCategory"` to `Migration:SkipTables` so the
worker leaves it alone. `ItemCategoryMigration` is complete and tested, so once the folder is staged you can
drop it from that list and let the worker own the table like the others.

### 2. Stage a run in Databricks

Import `databricks/stage_source_to_volume.py` as a notebook or job and fill in its widgets: the volume path,
source server, database and login. It refuses to run while they still hold `<placeholder>` values. Put the
source password in a secret scope rather than in the notebook:

```bash
databricks secrets create-scope <scope-name>
databricks secrets put-secret <scope-name> sql-password
```

It only selects the columns the target uses. That matters for `dbo.ItemNew`, which holds `varbinary(max)`
`Image` and `ImageThumbnail` columns that `dbo.Item` has no place for.

It also stages `Deleted` and `Active`. Without those columns `Migration:ExcludeDeletedItems` has nothing to
filter on, so the worker logs a warning naming the columns it did find rather than passing deleted rows
through silently.

### 3. Configure the worker

`appsettings.json` ships `<placeholder>` values only. Nothing environment-specific is committed. Supply the
real values with user secrets locally, or with `ConnectionStrings__TargetSql`,
`Databricks__PersonalAccessToken`, `Databricks__WorkspaceUrl` and `Migration__StageRootPath` environment
variables when deployed:

```bash
cd src/MixMix.MigrationWorker
dotnet user-secrets set "ConnectionStrings:TargetSql" "Server=tcp:<sql-server>.database.windows.net,1433;Initial Catalog=<target-database>;User ID=<sql-user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
dotnet user-secrets set "Databricks:WorkspaceUrl" "https://adb-<workspace-id>.<n>.azuredatabricks.net"
dotnet user-secrets set "Databricks:PersonalAccessToken" "<dapi-token>"
dotnet user-secrets set "Migration:StageRootPath" "/Volumes/<catalog>/<schema>/<volume>"
```

The worker refuses to start while the password, the workspace URL or the volume path is still a placeholder,
and says which setting is unfilled. This is deliberate: an unsubstituted workspace URL otherwise surfaces only
as `No such host is known`, which reads like a network outage rather than a missing setting. Note that the
`adb-123456789012345.7.azuredatabricks.net` host used throughout the Databricks documentation is an example,
not a real workspace — use the host from your own workspace's address bar. A personal access token starts with
`dapi`; an ADLS storage account key is not interchangeable with one, and the Files API rejects it with a bare
`401`.

The identity used by the worker needs `READ VOLUME` on the migration volume. For a service principal instead of
a token, set `Databricks:AuthMode` to `ServicePrincipal` and supply `TenantId`, `ClientId` and `ClientSecret`.

### 4. Run

```bash
dotnet run --project src/MixMix.MigrationWorker
```

To do a single pass and exit — the right shape for a scheduled container or an Azure Container App job — set
`Migration:RunOnce` to `true`. A failed pass sets a non-zero exit code.

### 5. Verify

`sql/02_verify_load.sql` reports the run history, target row counts, any orphaned rows, and whether the
`dbo.Item` identity seed is where it should be.

`sql/04_remove_source_deleted_items.sql` cleans up rows that reached the target because an extract omitted the
`Deleted` column.

## Settings

All under the `Migration` section.

| Setting | Default | Purpose |
| --- | --- | --- |
| `StageRootPath` | `/Volumes/<catalog>/<schema>/<volume>` | Volume folder holding run folders. Must be set. |
| `RunId` | *(empty)* | Run folder to load. Empty picks the newest matching folder by name. |
| `RunFolderPrefix` | `run_` | Restricts auto-discovery, so scratch folders like `stage` and `test` are ignored. |
| `SourceFolders` | `itemcategory`, `blob`, `item`, `itemblob` | Per-table folder names under the run folder. |
| `BatchSize` | `5000` | Rows buffered per `SqlBulkCopy` flush. |
| `Interval` | `01:00:00` | Time between passes. Ignored when `RunOnce` is true. |
| `RunOnce` | `false` | One pass, then stop the host. |
| `SkipCompletedTables` | `true` | Skip tables already recorded as succeeded for this run id. |
| `SkipTables` | *(empty)* | Tables loaded outside the worker, for example `["ItemCategory"]`. |
| `PruneMissingRows` | `false` | Delete target rows absent from the extract. Read the warning below first. |
| `ExcludeDeletedItems` | `true` | Drop source rows flagged `Deleted`. |
| `ExcludeInactiveItems` | `false` | Drop source rows flagged not `Active`. |
| `ReseedIdentityColumns` | `true` | Reseed `dbo.Item` and `dbo.ItemCategory` after explicit id inserts. |
| `EnsureMigrationObjects` | `true` | Create `dbo.MigrationTableRun` at startup if missing. |
| `AllowEmptySourceFolders` | `false` | Treat an empty staged folder as success instead of an error. |

## Reruns

`dbo.MigrationTableRun` records the outcome per `(RunId, TableName)`. If a pass fails partway, the tables that
already succeeded are skipped and the run resumes at the one that failed. The merges are upserts keyed on the
primary key, so re-running a table that partly landed converges rather than duplicating.

A pass stops at the first failing table, because everything after it depends on what did not load.

## Layout

```
src/MixMix.MigrationWorker/
  Configuration/    Options and validation
  Databricks/       Files API client, auth handler, temp-file spooling
  Parquet/          Row-group streaming and column access
  Sql/              Connection factory, bulk-copy data reader, run bookkeeping
  Sync/             Per-table migrations, shared pipeline, orchestrator
  MigrationSyncWorker.cs
tests/MixMix.MigrationWorker.Tests/
databricks/stage_source_to_volume.py
sql/
```

## Tests

```bash
dotnet test
```

Covers column access and type coercion, the per-table row mapping (id carry-over, truncation, `Deleted`
filtering, `NOT NULL` handling), volume path escaping, data-file filtering, and options validation. The Parquet
reader tests write and read real Parquet files rather than mocking the library, including a check that
unrequested columns are never decoded.

## Notes

- `Parquet.Net` is pinned to 5.x on purpose. Version 6 replaced the `DataColumn` API with a raw buffer API that
  requires managing definition levels and `IMemoryOwner` lifetimes by hand; 5.x's column reads are a better fit
  for reading a named subset of columns.
- Column names are matched case-insensitively and accept aliases (`ItemID`, `ItemId`, `Id`), so the loader does
  not break if the export job changes casing. A required column that is genuinely absent fails with the list of
  columns that were present.
