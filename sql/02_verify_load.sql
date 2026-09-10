/*
    Post-run checks against the target database. Pass the run id you loaded.
*/

DECLARE @RunId nvarchar(200) = N'run_20260101_001';

-- What the worker recorded for each table.
SELECT TableName,
       Status,
       AttemptCount,
       FilesProcessed,
       RowsRead,
       RowsStaged,
       RowsInserted,
       RowsUpdated,
       RowsFiltered,
       RowsRejected,
       RowsDeleted,
       ValuesTruncated,
       StartedUtc,
       CompletedUtc,
       ErrorMessage
FROM dbo.MigrationTableRun
WHERE RunId = @RunId
ORDER BY StartedUtc;

-- Row counts actually present in the target.
SELECT N'ItemCategory' AS TableName, COUNT_BIG(*) AS Rows FROM dbo.ItemCategory
UNION ALL SELECT N'Blob',     COUNT_BIG(*) FROM dbo.Blob
UNION ALL SELECT N'Item',     COUNT_BIG(*) FROM dbo.Item
UNION ALL SELECT N'ItemBlob', COUNT_BIG(*) FROM dbo.ItemBlob;

-- Referential integrity: all three of these must come back empty.
SELECT 'Item -> ItemCategory' AS Relationship, i.Id, i.CategoryId
FROM dbo.Item AS i
WHERE NOT EXISTS (SELECT 1 FROM dbo.ItemCategory AS c WHERE c.Id = i.CategoryId);

SELECT 'ItemBlob -> Item' AS Relationship, ib.ItemBlobId, ib.ItemId
FROM dbo.ItemBlob AS ib
WHERE NOT EXISTS (SELECT 1 FROM dbo.Item AS i WHERE i.Id = ib.ItemId);

SELECT 'ItemBlob -> Blob' AS Relationship, ib.ItemBlobId, ib.BlobId
FROM dbo.ItemBlob AS ib
WHERE NOT EXISTS (SELECT 1 FROM dbo.Blob AS b WHERE b.BlobId = ib.BlobId);

-- The identity seed must sit at or above the highest loaded id, or the next app insert collides.
SELECT CurrentIdentity = IDENT_CURRENT('dbo.Item'),
       MaxItemId       = (SELECT MAX(Id) FROM dbo.Item);
