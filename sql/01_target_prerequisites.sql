/*
    Run against the target database.

    Run once before the first sync pass.

    dbo.ItemCategory is expected to already exist, with the shape below. It is recorded here because two
    details drive the loader:

        Id    int           IDENTITY  -- so the load needs SET IDENTITY_INSERT to keep source Category ids
        Descr nvarchar(100) NULL      -- the source calls this column Name

    CREATE TABLE dbo.ItemCategory
    (
        Id    int IDENTITY(1,1) NOT NULL,
        Descr nvarchar(100)     NULL,
        CONSTRAINT PK_ItemCategory PRIMARY KEY CLUSTERED (Id)
    );
*/

/*
    Bookkeeping for the worker. The worker creates this itself when Migration:EnsureMigrationObjects is true,
    so this block only matters when that option is turned off (for example when the runtime login is
    intentionally restricted to DML only).
*/

IF OBJECT_ID(N'dbo.MigrationTableRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MigrationTableRun
    (
        RunId          nvarchar(200) NOT NULL,
        TableName      nvarchar(128) NOT NULL,
        Status         varchar(20)   NOT NULL,
        AttemptCount   int           NOT NULL CONSTRAINT DF_MigrationTableRun_AttemptCount DEFAULT (0),
        FilesProcessed int           NULL,
        RowsRead       bigint        NULL,
        RowsStaged     bigint        NULL,
        RowsInserted   bigint        NULL,
        RowsUpdated    bigint        NULL,
        RowsFiltered   bigint        NULL,
        RowsRejected   bigint        NULL,
        RowsDeleted    bigint        NULL,
        ValuesTruncated bigint       NULL,
        StartedUtc     datetime2(3)  NULL,
        CompletedUtc   datetime2(3)  NULL,
        ErrorMessage   nvarchar(max) NULL,
        CONSTRAINT PK_MigrationTableRun PRIMARY KEY CLUSTERED (RunId, TableName)
    );
END
GO

-- Columns added after the table first shipped, for a table created by an earlier version.
IF COL_LENGTH('dbo.MigrationTableRun', 'ValuesTruncated') IS NULL
    ALTER TABLE dbo.MigrationTableRun ADD ValuesTruncated bigint NULL;
GO

IF COL_LENGTH('dbo.MigrationTableRun', 'RowsDeleted') IS NULL
    ALTER TABLE dbo.MigrationTableRun ADD RowsDeleted bigint NULL;
GO

/*
    Both dbo.Item and dbo.ItemCategory have IDENTITY primary keys whose values are carried over from the
    source, which needs ALTER on those tables for SET IDENTITY_INSERT and DBCC CHECKIDENT.
    Grant the worker's login what it needs, nothing more.
*/

-- GRANT ALTER ON dbo.Item TO [<worker-login>];
-- GRANT ALTER ON dbo.ItemCategory TO [<worker-login>];
GO
