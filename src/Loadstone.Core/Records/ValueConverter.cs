using System.Collections.Concurrent;
using System.Globalization;
using Loadstone.Manifests;

namespace Loadstone.Records;

/// <summary>
/// Converts raw source strings to CLR values. A converter delegate is compiled once per
/// field definition and cached, so the per-row hot path is a single delegate invocation —
/// no reflection, no repeated switch dispatch.
/// </summary>
public static class ValueConverter
{
    private static readonly ConcurrentDictionary<FieldDefinition, Func<string, object?>> Cache = new();

    private static readonly string[] TrueTokens = ["true", "1", "y", "yes"];
    private static readonly string[] FalseTokens = ["false", "0", "n", "no"];

    /// <summary>
    /// Converts <paramref name="raw"/> for <paramref name="field"/>. Null/whitespace becomes
    /// the field default (or null). Returns false with an error message on parse failure.
    /// </summary>
    public static bool TryConvert(FieldDefinition field, string? raw, out object? value, out string? error)
    {
        error = null;
        var effective = string.IsNullOrWhiteSpace(raw) ? field.Default : raw;
        if (string.IsNullOrWhiteSpace(effective))
        {
            value = null;
            return true;
        }

        try
        {
            value = Cache.GetOrAdd(field, BuildConverter)(effective.Trim());
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            value = null;
            error = $"Value '{Truncate(effective)}' is not a valid {field.Type}.";
            return false;
        }
    }

    private static Func<string, object?> BuildConverter(FieldDefinition field)
    {
        var invariant = CultureInfo.InvariantCulture;
        return field.Type switch
        {
            FieldKind.String => static s => s,
            FieldKind.Int32 => s => int.Parse(s, NumberStyles.Integer, invariant),
            FieldKind.Int64 => s => long.Parse(s, NumberStyles.Integer, invariant),
            FieldKind.Decimal => s => decimal.Parse(s, NumberStyles.Number, invariant),
            FieldKind.Double => s => double.Parse(s, NumberStyles.Float, invariant),
            FieldKind.Boolean => ParseBoolean,
            FieldKind.Guid => static s => Guid.Parse(s),
            FieldKind.Date when field.Format is { } format =>
                s => DateOnly.ParseExact(s, format, invariant),
            FieldKind.Date => s => DateOnly.Parse(s, invariant),
            FieldKind.DateTime when field.Format is { } format =>
                s => DateTime.ParseExact(s, format, invariant, DateTimeStyles.None),
            FieldKind.DateTime => s => DateTime.Parse(s, invariant, DateTimeStyles.RoundtripKind),
            FieldKind.Time when field.Format is { } format =>
                s => TimeOnly.ParseExact(s, format, invariant),
            FieldKind.Time => s => TimeOnly.Parse(s, invariant),
            _ => static s => s,
        };
    }

    private static object ParseBoolean(string s) =>
        TrueTokens.Contains(s, StringComparer.OrdinalIgnoreCase) ? true
        : FalseTokens.Contains(s, StringComparer.OrdinalIgnoreCase) ? false
        : throw new FormatException($"'{s}' is not a recognized boolean token.");

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..57] + "...";
}
