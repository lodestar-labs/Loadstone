namespace Loadstone.Manifests;

/// <summary>
/// The single declarative definition of a dataset: where its data comes from, the entity
/// hierarchy it contains, how values convert and resolve, and which queue processes it.
/// Everything else in Loadstone (parsing expectations, staging DDL, merge statements,
/// validation, rejection reporting) is derived from this one document.
/// </summary>
public sealed class DatasetManifest
{
    public required string Name { get; set; }

    public string Version { get; set; } = "1";

    public string? Description { get; set; }

    public QueueSettings Queue { get; set; } = new();

    public SourceSettings Source { get; set; } = new();

    /// <summary>The root entity of the hierarchy. Children nest to any depth.</summary>
    public required EntityDefinition Root { get; set; }

    /// <summary>Queue that processes this dataset; defaults to the dataset name.</summary>
    public string QueueName => string.IsNullOrWhiteSpace(Queue.Name) ? Name : Queue.Name!;

    /// <summary>All entities in parent-before-child (breadth-first) order.</summary>
    public IEnumerable<EntityDefinition> EnumerateEntities()
    {
        var queue = new Queue<EntityDefinition>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var entity = queue.Dequeue();
            yield return entity;
            foreach (var child in entity.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    public EntityDefinition? FindEntity(string name) =>
        EnumerateEntities().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Structural validation. Returns an empty list when the manifest is usable;
    /// otherwise every problem found, so authors can fix them all in one pass.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Manifest 'name' is required.");
        }

        if (Root is null)
        {
            errors.Add("Manifest 'root' entity is required.");
            return errors;
        }

        var seenEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in EnumerateEntities())
        {
            ValidateEntity(entity, seenEntities, errors);
        }

        return errors;
    }

    private void ValidateEntity(EntityDefinition entity, HashSet<string> seenEntities, List<string> errors)
    {
        var scope = $"Entity '{entity.Name}'";
        if (string.IsNullOrWhiteSpace(entity.Name))
        {
            errors.Add("Every entity requires a 'name'.");
        }
        else if (!seenEntities.Add(entity.Name))
        {
            errors.Add($"{scope}: entity names must be unique within a dataset.");
        }

        if (string.IsNullOrWhiteSpace(entity.Table))
        {
            errors.Add($"{scope}: 'table' is required.");
        }

        if (string.IsNullOrWhiteSpace(entity.KeyColumn))
        {
            errors.Add($"{scope}: 'keyColumn' is required.");
        }

        if (!ReferenceEquals(entity, Root) && string.IsNullOrWhiteSpace(entity.ParentKeyColumn))
        {
            errors.Add($"{scope}: child entities require 'parentKeyColumn' (the foreign key to the parent).");
        }

        if (entity.Fields.Count == 0)
        {
            errors.Add($"{scope}: at least one field is required.");
        }

        var seenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in entity.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                errors.Add($"{scope}: every field requires a 'name'.");
                continue;
            }

            if (!seenFields.Add(field.Name))
            {
                errors.Add($"{scope}: duplicate field name '{field.Name}'.");
            }

            if (!seenColumns.Add(field.ColumnName))
            {
                errors.Add($"{scope}: duplicate target column '{field.ColumnName}'.");
            }

            if (string.Equals(field.ColumnName, entity.KeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{scope}.{field.Name}: column '{field.ColumnName}' collides with the entity's keyColumn (the key is database-generated).");
            }

            if (entity.ParentKeyColumn is { } parentKeyColumn
                && string.Equals(field.ColumnName, parentKeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{scope}.{field.Name}: column '{field.ColumnName}' collides with parentKeyColumn (the foreign key is wired automatically).");
            }

            if (field.Lookup is { } lookup && string.IsNullOrWhiteSpace(lookup.List))
            {
                errors.Add($"{scope}.{field.Name}: lookup requires a 'list' name.");
            }

            if (field.Lookup is { OnMissing: LookupMissingPolicy.UseDefault, Default: null })
            {
                errors.Add($"{scope}.{field.Name}: lookup policy 'UseDefault' requires a 'default' value.");
            }

            if (field.Lookup is null && field.Type == FieldKind.Decimal)
            {
                if (field.Precision is < 1 or > 38)
                {
                    errors.Add($"{scope}.{field.Name}: decimal precision must be between 1 and 38.");
                }
                else if (field.Scale < 0 || field.Scale > field.Precision)
                {
                    errors.Add($"{scope}.{field.Name}: decimal scale must be between 0 and the precision ({field.Precision}).");
                }
            }

            if (field.MaxLength is { } maxLength && (maxLength < 1 || maxLength > 4000))
            {
                errors.Add($"{scope}.{field.Name}: maxLength must be between 1 and 4000 (omit it for unbounded text).");
            }
        }

        foreach (var key in entity.NaturalKey)
        {
            var keyField = entity.Fields.FirstOrDefault(f => string.Equals(f.ColumnName, key, StringComparison.OrdinalIgnoreCase));
            if (keyField is null)
            {
                errors.Add($"{scope}: natural key column '{key}' does not match any field column.");
                continue;
            }

            if (keyField.Lookup is null && keyField.Type == FieldKind.String && keyField.MaxLength is > 850)
            {
                errors.Add($"{scope}: natural key column '{key}' allows more than 850 characters, which exceeds SQL Server's index key size.");
            }
        }
    }
}

public sealed class QueueSettings
{
    /// <summary>Queue name; null means the dataset gets its own queue named after it.</summary>
    public string? Name { get; set; }

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base delay for exponential retry backoff (delay = base * 2^attempt).</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>Concurrent jobs processed from this queue.</summary>
    public int Concurrency { get; set; } = 1;
}

public sealed class SourceSettings
{
    /// <summary>Formats accepted for this dataset. Empty means all supported formats.</summary>
    public List<string> Formats { get; set; } = [];

    public XmlSourceSettings Xml { get; set; } = new();

    public JsonSourceSettings Json { get; set; } = new();

    public CsvSourceSettings Csv { get; set; } = new();

    public bool AcceptsFormat(string format) =>
        Formats.Count == 0 || Formats.Contains(format, StringComparer.OrdinalIgnoreCase);
}

public sealed class XmlSourceSettings
{
    /// <summary>
    /// Optional wrapper element containing root-entity elements. When null, root-entity
    /// elements are matched by name at any depth.
    /// </summary>
    public string? RootElement { get; set; }
}

public sealed class JsonSourceSettings
{
    /// <summary>
    /// Property on the top-level object holding the record array. When null the document
    /// itself must be an array, or an object with a property named after the root entity.
    /// </summary>
    public string? RootProperty { get; set; }
}

public sealed class CsvSourceSettings
{
    public char Delimiter { get; set; } = ',';

    public bool HasHeaderRow { get; set; } = true;

    /// <summary>Row-key column linking child rows to parents in hierarchical (zip) uploads.</summary>
    public string KeyColumn { get; set; } = "_key";

    public string ParentKeyColumn { get; set; } = "_parentKey";

    /// <summary>
    /// Text encoding of the file (an IANA name such as "utf-8", "windows-1252", or
    /// "iso-8859-1"). A byte-order mark always wins when present. Defaults to UTF-8.
    /// </summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>
    /// Accept rows whose field count differs from the header. Off by default: a ragged row
    /// usually means a shifted or corrupted line, and importing it silently would store
    /// wrong data — so it is rejected with a row-level error instead.
    /// </summary>
    public bool AllowRaggedRows { get; set; }
}
