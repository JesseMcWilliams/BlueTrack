using System.Collections;
using System.Data.Common;

namespace BlueTrack.Api.Data;

/// <summary>One column a <see cref="ListDataReader{T}"/> reads: its name, its type, and how to take its value from a row.</summary>
public sealed record ListDataColumn<T>(string Name, Type Type, Func<T, object?> Value);

/// <summary>
/// D-355: a forward-only reader over a list of rows, for <c>SqlBulkCopy</c>, so a bulk insert reads the rows as they are
/// instead of a <c>DataTable</c> copy of them (which holds every row a second time, each value boxed). A null value reads as DBNull.
/// </summary>
public sealed class ListDataReader<T>(IEnumerable<T> rows, IReadOnlyList<ListDataColumn<T>> columns) : DbDataReader
{
    private readonly IEnumerator<T> _rows = rows.GetEnumerator();
    private bool _onRow;
    private bool _closed;

    public override bool Read() => _onRow = _rows.MoveNext();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override bool NextResult() => false;
    public override int FieldCount => columns.Count;
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => -1;

    public override string GetName(int ordinal) => columns[ordinal].Name;
    public override Type GetFieldType(int ordinal) => columns[ordinal].Type;
    public override string GetDataTypeName(int ordinal) => columns[ordinal].Type.Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new IndexOutOfRangeException($"No column named '{name}'.");
    }

    public override object GetValue(int ordinal)
    {
        if (!_onRow) throw new InvalidOperationException("Read() hasn't moved to a row.");
        return columns[ordinal].Value(_rows.Current) ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, columns.Count);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

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

    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        _rows.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
}
