/*
    Seeds the target dbo.ItemCategory from the source dbo.Category.

    WHY THIS EXISTS
    ---------------
    Every other table reaches the target through the Databricks volume. If your staged run has no
    'itemcategory' folder, this table can be loaded straight from the source instead, so that
    dbo.Item.CategoryId has parents to point at. Azure SQL has no cross-database queries, so the source rows
    are inlined below rather than selected across databases.

    HOW TO USE
    ----------
    1. Capture your own categories from the source database:

           SELECT Id, Name FROM dbo.Category ORDER BY Id;

    2. Replace the sample VALUES list below with that output.
    3. Run this script against the target database.
    4. Add "ItemCategory" to Migration:SkipTables so the worker leaves the table alone.

    dbo.ItemCategory.Id is an IDENTITY column, and dbo.Item.CategoryId references it, so the source ids are
    inserted explicitly under IDENTITY_INSERT and the seed is reset afterwards. That needs ALTER on the table.

    Load categories regardless of their Active/Deleted flags: omitting one would orphan every item that points
    at it, and the target has no column to carry the flag.

    Safe to re-run: the MERGE upserts on Id.

    Once the export job stages an 'itemcategory' folder, drop "ItemCategory" from Migration:SkipTables and let
    the worker own this table like the others.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Category TABLE (Id int NOT NULL PRIMARY KEY, Descr nvarchar(100) NULL);

-- Sample rows. Replace with the output of the SELECT above; ids need not be contiguous.
INSERT INTO @Category (Id, Descr) VALUES
    (1, N'CATEGORY_ONE'),
    (2, N'CATEGORY_TWO'),
    (3, N'CATEGORY_THREE');

BEGIN TRANSACTION;

SET IDENTITY_INSERT dbo.ItemCategory ON;

MERGE dbo.ItemCategory WITH (HOLDLOCK) AS t
USING @Category AS s
    ON t.Id = s.Id
WHEN MATCHED AND EXISTS (SELECT s.Descr EXCEPT SELECT t.Descr)
    THEN UPDATE SET t.Descr = s.Descr
WHEN NOT MATCHED BY TARGET
    THEN INSERT (Id, Descr) VALUES (s.Id, s.Descr);

SET IDENTITY_INSERT dbo.ItemCategory OFF;

COMMIT TRANSACTION;

-- Set the seed explicitly: a bare RESEED only ever raises it, never lowers it back onto the loaded rows.
DECLARE @maxCategoryId int = (SELECT ISNULL(MAX(Id), 0) FROM dbo.ItemCategory);

DBCC CHECKIDENT ('dbo.ItemCategory', RESEED, @maxCategoryId) WITH NO_INFOMSGS;

SELECT Id, Descr FROM dbo.ItemCategory ORDER BY Id;
