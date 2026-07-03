namespace Loadstone.Manifests;

/// <summary>
/// One level of the hierarchy: a source record shape and the relational table it lands in.
/// </summary>
public sealed class EntityDefinition
{
    /// <summary>Matches the XML element name, JSON property name, or CSV file name.</summary>
    public required string Name { get; set; }

    public required string Table { get; set; }

    public string Schema { get; set; } = "dbo";

    /// <summary>Surrogate identity primary key column in the target table.</summary>
    public string KeyColumn { get; set; } = "Id";

    /// <summary>Foreign key column referencing the parent entity's key. Null on the root.</summary>
    public string? ParentKeyColumn { get; set; }

    /// <summary>
    /// Target columns that identify a row for upsert (scoped within the parent row).
    /// Empty means rows are always inserted, never updated.
    /// </summary>
    public List<string> NaturalKey { get; set; } = [];

    /// <summary>When true, a parent record without at least one instance of this child is rejected.</summary>
    public bool Required { get; set; }

    public List<FieldDefinition> Fields { get; set; } = [];

    public List<EntityDefinition> Children { get; set; } = [];

    public FieldDefinition? FindField(string sourceName) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, sourceName, StringComparison.OrdinalIgnoreCase));

    public EntityDefinition? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public string QualifiedTable => $"[{Schema.Replace("]", "]]")}].[{Table.Replace("]", "]]")}]";
}
