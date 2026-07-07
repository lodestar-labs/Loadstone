using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Records;

namespace Loadstone.Runtime.Steps;

/// <summary>
/// First built-in step: converts raw source strings to typed values, applies defaults and
/// constants, and enforces required fields, maximum lengths, and required child entities.
/// Problems are recorded on the record; they never throw, so one bad record can't take
/// down an import.
/// </summary>
public sealed class ValidateStep : IImportStep
{
    public string Name => "validate";

    public int Order => 100;

    public ValueTask ExecuteAsync(ImportContext context, DataRecord root, CancellationToken cancellationToken = default)
    {
        foreach (var record in root.SelfAndDescendants())
        {
            ValidateRecord(record);
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateRecord(DataRecord record)
    {
        var entity = record.Entity;
        foreach (var field in entity.Fields)
        {
            record.Raw.TryGetValue(field.Name, out var raw);
            if (field.Source == FieldSource.Constant)
            {
                raw = field.Default;
            }

            if (field.Lookup is not null)
            {
                // Conversion happens in the lookup step; only the presence check applies
                // here. lookup.Default is a fallback for *unknown* values, not missing
                // ones, so it does not satisfy a required field.
                if (field.Required && string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(field.Default))
                {
                    record.AddError(field.Name, "Required value is missing.");
                }

                continue;
            }

            if (!ValueConverter.TryConvert(field, raw, out var value, out var error))
            {
                record.AddError(field.Name, error!, raw);
                continue;
            }

            if (value is null)
            {
                if (field.Required)
                {
                    record.AddError(field.Name, "Required value is missing.");
                }

                record.Values[field.ColumnName] = null;
                continue;
            }

            // Length limit: the explicit maxLength, or — for natural-key strings, which get a
            // fixed-length indexable column — the same default the DDL uses. Without this, a
            // long key value passes validation and aborts the whole import at merge time.
            var effectiveMaxLength = field.MaxLength
                ?? (IsNaturalKeyMember(entity, field) ? FieldDefinition.IndexableStringDefaultLength : (int?)null);
            if (value is string text && effectiveMaxLength is { } maxLength && text.Length > maxLength)
            {
                record.AddError(field.Name, $"Value is {text.Length} characters; the maximum is {maxLength}.", raw);
                continue;
            }

            // The target column is decimal(Precision,Scale); a value whose integer part needs
            // more digits than Precision-Scale would raise arithmetic overflow during bulk
            // copy and abort the whole import instead of rejecting this one row. The digits
            // are counted AFTER rounding to the column's scale, because SQL rounds on insert:
            // 99.999 into decimal(4,2) becomes 100.00, which no longer fits.
            if (value is decimal number)
            {
                var scaled = Math.Round(Math.Abs(number), Math.Min(field.Scale, 28), MidpointRounding.AwayFromZero);
                var integerPart = decimal.Truncate(scaled);
                var integerDigits = integerPart == 0m
                    ? 0
                    : integerPart.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
                var allowedIntegerDigits = field.Precision - field.Scale;
                if (integerDigits > allowedIntegerDigits)
                {
                    record.AddError(
                        field.Name,
                        $"Value has {integerDigits} integer digits after rounding to scale {field.Scale}; decimal({field.Precision},{field.Scale}) allows at most {allowedIntegerDigits}.",
                        raw);
                    continue;
                }
            }

            record.Values[field.ColumnName] = value;
        }

        foreach (var child in entity.Children)
        {
            if (child.Required && !record.Children.Any(c => ReferenceEquals(c.Entity, child)))
            {
                record.AddError(child.Name, $"At least one '{child.Name}' child is required.");
            }
        }
    }

    private static bool IsNaturalKeyMember(EntityDefinition entity, FieldDefinition field) =>
        entity.NaturalKey.Any(k => string.Equals(k, field.ColumnName, StringComparison.OrdinalIgnoreCase));
}
