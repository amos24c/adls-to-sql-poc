/*
    Removes target items that the source marks as deleted.

    WHEN YOU NEED THIS
    ------------------
    Migration:ExcludeDeletedItems filters rows flagged Deleted, but it can only do that if the staged extract
    actually carries a Deleted column. If an extract omitted it, those rows reached the target and the worker
    logged a warning naming the columns it did find. This script cleans them up after the fact.

    The merges only insert and update, so re-running a sync will not remove them either. Two ways forward:

      - run this script once, and add Deleted to the export job's column list so it cannot recur
        (databricks/stage_source_to_volume.py already stages Deleted and Active); or
      - turn on Migration:PruneMissingRows, so a later run against an extract that correctly filters deleted
        rows deletes them from the target as well.

    Until the extract carries Deleted, every sync pass re-inserts these rows.

    HOW TO USE
    ----------
    1. List the deleted ids in the source database:

           SELECT ItemID, Name FROM dbo.ItemNew WHERE Deleted = 1 ORDER BY ItemID;

    2. Replace the sample VALUES list below with those ids.
    3. Run this script against the target database.

    The dbo.ItemBlob delete comes first because those rows reference the items being removed.
    Safe to re-run.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DeletedItems TABLE (Id int NOT NULL PRIMARY KEY);

-- Sample ids. Replace with the output of the SELECT above.
INSERT INTO @DeletedItems (Id) VALUES (1), (2), (3);

BEGIN TRANSACTION;

DELETE b
FROM dbo.ItemBlob AS b
WHERE EXISTS (SELECT 1 FROM @DeletedItems AS d WHERE d.Id = b.ItemId);

DECLARE @childRows int = @@ROWCOUNT;

DELETE i
FROM dbo.Item AS i
WHERE EXISTS (SELECT 1 FROM @DeletedItems AS d WHERE d.Id = i.Id);

DECLARE @itemRows int = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT ItemBlobRowsDeleted = @childRows,
       ItemRowsDeleted     = @itemRows,
       ItemsRemaining      = (SELECT COUNT_BIG(*) FROM dbo.Item),
       StillPresent        = (SELECT COUNT_BIG(*) FROM dbo.Item AS i
                              WHERE EXISTS (SELECT 1 FROM @DeletedItems AS d WHERE d.Id = i.Id));
