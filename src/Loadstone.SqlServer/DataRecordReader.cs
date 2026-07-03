using System.Collections;
using System.Data.Common;
using Loadstone.Manifests;
using Loadstone.Records;

namespace Loadstone.SqlServer;

/// <summary>
/// Bridges the pipeline's async record stream directly into <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/>
/// as a forward-only data reader. Rows flow from the source file to the staging table
/// without ever being materialized in a batch structure, so imports of any size run in
/// constant memory on the flat path.
/// </summary>
internal sealed class DataRecordReader(
    IAsyncEnumerable<DataRecord> source,
    EntityDefinition entity,
    CancellationToken cancellationToken) : DbDataReader
{
    private readonly IAsyncEnumerator<DataRecord> _source = source.GetAsyncEnumerator(cancellationToken);
    private readonly FieldDefinition[] _fields = [.. entity.Fields];
    private DataRecord? _current;
    private bool _closed;

    public long RowsRead { get; private set; }

    public override int FieldCount => _fields.Length;

    public override bool HasRows => true;

    public override bool IsClosed => _closed;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override async Task<bool> ReadAsync(CancellationToken token)
    {
        if (!await _source.MoveNextAsync())
        {
            _current = null;
            return false;
        }

        _current = _source.Current;
        RowsRead++;
        return true;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object GetValue(int ordinal)
    {
        var record = _current ?? throw new InvalidOperationException("No current row.");
        record.Values.TryGetValue(_fields[ordinal].ColumnName, out var value);
        return SqlTypeMap.ToStagingValue(value) ?? DBNull.Value;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override string GetName(int ordinal) => _fields[ordinal].ColumnName;

    public override int GetOrdinal(string name)
    {
        var index = Array.FindIndex(_fields, f => string.Equals(f.ColumnName, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new IndexOutOfRangeException($"Unknown column '{name}'.");
    }

    public override Type GetFieldType(int ordinal) => SqlTypeMap.ClrTypeFor(_fields[ordinal]);

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    public override bool NextResult() => false;

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
