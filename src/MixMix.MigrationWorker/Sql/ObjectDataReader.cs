using System.Data;

namespace MixMix.MigrationWorker.Sql;

/// <summary>
/// Adapts a sequence of records to the <see cref="IDataReader"/> surface that SqlBulkCopy consumes,
/// so rows stream into the staging table without an intermediate DataTable.
/// </summary>
internal sealed class ObjectDataReader<T>(IEnumerable<T> rows, IReadOnlyList<StagingColumn<T>> columns) : IDataReader
{
    private readonly IEnumerator<T> _enumerator = rows.GetEnumerator();
    private bool _closed;

    public int FieldCount => columns.Count;

    public bool IsClosed => _closed;

    public int Depth => 0;

    public int RecordsAffected => -1;

    public bool Read()
    {
        if (_closed)
        {
            return false;
        }

        if (_enumerator.MoveNext())
        {
            return true;
        }

        _closed = true;
        return false;
    }

    public object GetValue(int i) => columns[i].GetValue(_enumerator.Current) ?? DBNull.Value;

    public bool IsDBNull(int i) => columns[i].GetValue(_enumerator.Current) is null;

    public string GetName(int i) => columns[i].Name;

    public Type GetFieldType(int i) => columns[i].FieldType;

    public string GetDataTypeName(int i) => columns[i].FieldType.Name;

    public int GetOrdinal(string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"Column '{name}' is not part of this reader.");
    }

    public int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, columns.Count);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public object this[int i] => GetValue(i);

    public object this[string name] => GetValue(GetOrdinal(name));

    public void Close() => _closed = true;

    public void Dispose()
    {
        _closed = true;
        _enumerator.Dispose();
    }

    public bool NextResult() => false;

    public DataTable? GetSchemaTable() => null;

    public bool GetBoolean(int i) => (bool)GetValue(i);

    public byte GetByte(int i) => (byte)GetValue(i);

    public char GetChar(int i) => (char)GetValue(i);

    public DateTime GetDateTime(int i) => (DateTime)GetValue(i);

    public decimal GetDecimal(int i) => (decimal)GetValue(i);

    public double GetDouble(int i) => (double)GetValue(i);

    public float GetFloat(int i) => (float)GetValue(i);

    public Guid GetGuid(int i) => (Guid)GetValue(i);

    public short GetInt16(int i) => (short)GetValue(i);

    public int GetInt32(int i) => (int)GetValue(i);

    public long GetInt64(int i) => (long)GetValue(i);

    public string GetString(int i) => (string)GetValue(i);

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) =>
        throw new NotSupportedException();

    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) =>
        throw new NotSupportedException();

    public IDataReader GetData(int i) => throw new NotSupportedException();
}
