using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MixMix.MigrationWorker.Configuration;
using MixMix.MigrationWorker.Sync;

namespace MixMix.MigrationWorker.Sql;

/// <summary>
/// Tracks per-table outcomes in the target database so an interrupted run can be re-driven
/// without redoing the tables that already landed.
/// </summary>
public sealed class MigrationRunStore(ISqlConnectionFactory connectionFactory, IOptions<MigrationOptions> options)
{
    private const string TableName = "dbo.MigrationTableRun";

    private readonly MigrationOptions _options = options.Value;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, $"""
            IF OBJECT_ID(N'{TableName}', N'U') IS NULL
            BEGIN
                CREATE TABLE {TableName}
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

            -- Columns added after the table first shipped, so a worker running against a table created by an
            -- earlier version does not fail on an unknown column.
            IF COL_LENGTH('{TableName}', 'ValuesTruncated') IS NULL
                ALTER TABLE {TableName} ADD ValuesTruncated bigint NULL;

            IF COL_LENGTH('{TableName}', 'RowsDeleted') IS NULL
                ALTER TABLE {TableName} ADD RowsDeleted bigint NULL;
            """);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasSucceededAsync(string runId, string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {TableName}
                WHERE RunId = @RunId AND TableName = @TableName AND Status = 'Succeeded'
            ) THEN 1 ELSE 0 END
            """);

        command.Parameters.AddWithValue("@RunId", runId);
        command.Parameters.AddWithValue("@TableName", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) == 1;
    }

    public async Task MarkRunningAsync(string runId, string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, $"""
            MERGE {TableName} WITH (HOLDLOCK) AS t
            USING (SELECT @RunId AS RunId, @TableName AS TableName) AS s
                ON t.RunId = s.RunId AND t.TableName = s.TableName
            WHEN MATCHED THEN UPDATE SET
                t.Status = 'Running',
                t.AttemptCount = t.AttemptCount + 1,
                t.StartedUtc = SYSUTCDATETIME(),
                t.CompletedUtc = NULL,
                t.ErrorMessage = NULL
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (RunId, TableName, Status, AttemptCount, StartedUtc)
                VALUES (s.RunId, s.TableName, 'Running', 1, SYSUTCDATETIME());
            """);

        command.Parameters.AddWithValue("@RunId", runId);
        command.Parameters.AddWithValue("@TableName", tableName);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSucceededAsync(
        string runId,
        TableMigrationResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, $"""
            UPDATE {TableName}
            SET Status = 'Succeeded',
                FilesProcessed = @FilesProcessed,
                RowsRead = @RowsRead,
                RowsStaged = @RowsStaged,
                RowsInserted = @RowsInserted,
                RowsUpdated = @RowsUpdated,
                RowsFiltered = @RowsFiltered,
                RowsRejected = @RowsRejected,
                RowsDeleted = @RowsDeleted,
                ValuesTruncated = @ValuesTruncated,
                CompletedUtc = SYSUTCDATETIME(),
                ErrorMessage = NULL
            WHERE RunId = @RunId AND TableName = @TableName;
            """);

        command.Parameters.AddWithValue("@RunId", runId);
        command.Parameters.AddWithValue("@TableName", result.TableName);
        command.Parameters.AddWithValue("@FilesProcessed", result.FilesProcessed);
        command.Parameters.AddWithValue("@RowsRead", result.RowsRead);
        command.Parameters.AddWithValue("@RowsStaged", result.RowsStaged);
        command.Parameters.AddWithValue("@RowsInserted", result.RowsInserted);
        command.Parameters.AddWithValue("@RowsUpdated", result.RowsUpdated);
        command.Parameters.AddWithValue("@RowsFiltered", result.RowsFiltered);
        command.Parameters.AddWithValue("@RowsRejected", result.RowsRejected);
        command.Parameters.AddWithValue("@RowsDeleted", result.RowsDeleted);
        command.Parameters.AddWithValue("@ValuesTruncated", result.ValuesTruncated);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        string runId,
        string tableName,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, $"""
            UPDATE {TableName}
            SET Status = 'Failed',
                CompletedUtc = SYSUTCDATETIME(),
                ErrorMessage = @ErrorMessage
            WHERE RunId = @RunId AND TableName = @TableName;
            """);

        command.Parameters.AddWithValue("@RunId", runId);
        command.Parameters.AddWithValue("@TableName", tableName);
        command.Parameters.AddWithValue("@ErrorMessage", errorMessage);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqlCommand CreateCommand(SqlConnection connection, string commandText) =>
        new(commandText, connection) { CommandTimeout = (int)_options.SqlCommandTimeout.TotalSeconds };
}
