using Loadstone.Manifests;

namespace Loadstone.SqlServer;

/// <summary>Maps manifest field kinds to SQL Server column types and CLR staging types.</summary>
internal static class SqlTypeMap
{
    /// <summary>
    /// SQL column type for a field. Lookup fields store the resolved value, so their
    /// column type follows the lookup's valueKind (int for the built-in code lists).
    /// </summary>
    public static string SqlTypeFor(FieldDefinition field, bool indexable = false)
    {
        var kind = field.Lookup?.ValueKind ?? field.Type;
        return kind switch
        {
            FieldKind.String => $"nvarchar({StringLength(field, indexable)})",
            FieldKind.Int32 => "int",
            FieldKind.Int64 => "bigint",
            FieldKind.Decimal => $"decimal({field.Precision},{field.Scale})",
            FieldKind.Double => "float",
            FieldKind.Boolean => "bit",
            FieldKind.Date => "date",
            FieldKind.DateTime => "datetime2",
            FieldKind.Time => "time",
            FieldKind.Guid => "uniqueidentifier",
            _ => "nvarchar(max)",
        };
    }

    /// <summary>CLR type used in staging DataTables (bulk copy friendly).</summary>
    public static Type ClrTypeFor(FieldDefinition field)
    {
        var kind = field.Lookup?.ValueKind ?? field.Type;
        return kind switch
        {
            FieldKind.String => typeof(string),
            FieldKind.Int32 => typeof(int),
            FieldKind.Int64 => typeof(long),
            FieldKind.Decimal => typeof(decimal),
            FieldKind.Double => typeof(double),
            FieldKind.Boolean => typeof(bool),
            FieldKind.Date => typeof(DateTime),
            FieldKind.DateTime => typeof(DateTime),
            FieldKind.Time => typeof(TimeSpan),
            FieldKind.Guid => typeof(Guid),
            _ => typeof(string),
        };
    }

    /// <summary>Converts pipeline values (DateOnly/TimeOnly) into bulk-copy friendly types.</summary>
    public static object? ToStagingValue(object? value) =>
        value switch
        {
            null => null,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            TimeOnly time => time.ToTimeSpan(),
            _ => value,
        };

    private static string StringLength(FieldDefinition field, bool indexable) =>
        field.MaxLength is { } max ? max.ToString()
        : indexable ? "400"
        : "max";
}
