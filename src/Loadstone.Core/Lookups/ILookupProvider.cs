namespace Loadstone.Lookups;

/// <summary>
/// Resolves raw source values against a named lookup list — the generalization of the
/// classic code-list table. Implement this to plug in any resolution source: a database
/// table, a REST API, a static dictionary, another service.
/// </summary>
public interface ILookupProvider
{
    /// <summary>Provider key referenced by manifests (field.lookup.provider). Built-in: "codelist".</summary>
    string Key { get; }

    ValueTask<LookupResult> ResolveAsync(string list, string value, bool caseInsensitive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a missing entry (policy AutoCreate) and returns its resolved value.
    /// Providers that cannot create entries should throw <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<object?> CreateAsync(string list, string value, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Lookup provider '{Key}' does not support creating entries.");
}

public readonly record struct LookupResult(bool Found, object? Value)
{
    public static LookupResult Hit(object? value) => new(true, value);

    public static readonly LookupResult Miss = new(false, null);
}

/// <summary>
/// Applies manifest lookup settings (provider selection, caching, missing-value policy)
/// on top of the registered <see cref="ILookupProvider"/>s.
/// </summary>
public interface ILookupResolver
{
    /// <summary>
    /// Resolves <paramref name="rawValue"/> per the field's lookup settings.
    /// Outcome semantics: Resolved carries the value; RecordRejected means the caller marks the
    /// record; FileRejected must abort the import.
    /// </summary>
    ValueTask<LookupOutcome> ResolveAsync(Manifests.FieldDefinition field, string rawValue, CancellationToken cancellationToken = default);
}

public enum LookupOutcomeKind
{
    Resolved,
    RecordRejected,
    FileRejected,
}

public readonly record struct LookupOutcome(LookupOutcomeKind Kind, object? Value, string? Message)
{
    public static LookupOutcome Resolved(object? value) => new(LookupOutcomeKind.Resolved, value, null);

    public static LookupOutcome RejectRecord(string message) => new(LookupOutcomeKind.RecordRejected, null, message);

    public static LookupOutcome RejectFile(string message) => new(LookupOutcomeKind.FileRejected, null, message);
}

/// <summary>Raised when an unknown lookup value must abort the whole import (policy RejectFile).</summary>
public sealed class LookupRejectedFileException(string message) : Exception(message);
