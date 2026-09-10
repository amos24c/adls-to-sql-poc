namespace MixMix.MigrationWorker.Sql;

/// <summary>Maps one staging-table column to a value on the record being loaded.</summary>
/// <param name="Name">Column name in the staging table.</param>
/// <param name="FieldType">Non-nullable CLR type of the column, used for the bulk copy schema.</param>
/// <param name="GetValue">Reads the value off a record; return null for a database null.</param>
public sealed record StagingColumn<T>(string Name, Type FieldType, Func<T, object?> GetValue);
