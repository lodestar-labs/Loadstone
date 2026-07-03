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
                // Conversion happens in the lookup step; only the presence check applies here.
                if (field.Required && string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(field.Lookup.Default ?? field.Default))
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

            if (value is string text && field.MaxLength is { } maxLength && text.Length > maxLength)
            {
                record.AddError(field.Name, $"Value is {text.Length} characters; the maximum is {maxLength}.", raw);
                continue;
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
}
