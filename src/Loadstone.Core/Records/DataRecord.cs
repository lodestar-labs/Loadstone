using Loadstone.Manifests;

namespace Loadstone.Records;

/// <summary>
/// The canonical in-flight record: one entity instance with converted values and its
/// children. Every source format (XML, JSON, CSV) normalizes to this shape, so the rest
/// of the pipeline is format-agnostic.
/// </summary>
public sealed class DataRecord
{
    public required EntityDefinition Entity { get; init; }

    public SourceLocation Location { get; init; }

    /// <summary>Raw source strings keyed by field name, kept for rejection reports.</summary>
    public Dictionary<string, string?> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Converted values keyed by target column name.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DataRecord> Children { get; } = [];

    public List<RecordIssue> Issues { get; } = [];

    /// <summary>Database key assigned by the writer after the row is merged.</summary>
    public object? DatabaseKey { get; set; }

    /// <summary>True when this record (not counting children) carries at least one error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>True when this record or any descendant carries an error.</summary>
    public bool TreeHasErrors => SelfAndDescendants().Any(r => r.HasErrors);

    public void AddError(string field, string message, string? rawValue = null) =>
        Issues.Add(new RecordIssue(IssueSeverity.Error, field, message, rawValue));

    public void AddWarning(string field, string message, string? rawValue = null) =>
        Issues.Add(new RecordIssue(IssueSeverity.Warning, field, message, rawValue));

    public IEnumerable<DataRecord> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>Where a record came from in its source document, for diagnostics.</summary>
public readonly record struct SourceLocation(long? Line, string? Path)
{
    public override string ToString() =>
        (Line, Path) switch
        {
            (not null, not null) => $"{Path} (line {Line})",
            (not null, null) => $"line {Line}",
            (null, not null) => Path!,
            _ => "unknown",
        };
}

public enum IssueSeverity
{
    Warning,
    Error,
}

public sealed record RecordIssue(IssueSeverity Severity, string Field, string Message, string? RawValue);
