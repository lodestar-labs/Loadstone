namespace Loadstone.Manifests;

public enum FieldKind
{
    String,
    Int32,
    Int64,
    Decimal,
    Double,
    Boolean,
    Date,
    DateTime,
    Time,
    Guid,
}

/// <summary>Where a field's raw value is read from in the source document.</summary>
public enum FieldSource
{
    /// <summary>Child element text (XML), property (JSON), or column (CSV).</summary>
    Element,

    /// <summary>XML attribute on the entity element. Ignored by JSON/CSV readers.</summary>
    Attribute,

    /// <summary>The manifest-supplied <see cref="FieldDefinition.Default"/> constant.</summary>
    Constant,
}

public sealed class FieldDefinition
{
    /// <summary>
    /// Column length given to a string field with no explicit <see cref="MaxLength"/> when it
    /// must be indexable (natural-key membership). Validation and DDL must agree on this
    /// number: a value that fits validation but not the column aborts the whole import at
    /// merge time instead of rejecting one row.
    /// </summary>
    public const int IndexableStringDefaultLength = 400;

    /// <summary>Source name: XML element/attribute, JSON property, or CSV header.</summary>
    public required string Name { get; set; }

    /// <summary>Target column; defaults to the source name.</summary>
    public string? Column { get; set; }

    public FieldKind Type { get; set; } = FieldKind.String;

    public FieldSource Source { get; set; } = FieldSource.Element;

    public bool Required { get; set; }

    public int? MaxLength { get; set; }

    /// <summary>Decimal precision/scale for <see cref="FieldKind.Decimal"/> columns.</summary>
    public int Precision { get; set; } = 18;

    public int Scale { get; set; } = 6;

    /// <summary>Optional exact date/time format (e.g. "yyyyMMdd"); invariant parsing otherwise.</summary>
    public string? Format { get; set; }

    /// <summary>Value used when the source value is missing, and by <see cref="FieldSource.Constant"/>.</summary>
    public string? Default { get; set; }

    public LookupSettings? Lookup { get; set; }

    public string ColumnName => string.IsNullOrWhiteSpace(Column) ? Name : Column!;
}

public enum LookupMissingPolicy
{
    /// <summary>Unknown value fails the whole import job.</summary>
    RejectFile,

    /// <summary>Unknown value rejects only the affected record (and its children) into the rejection store.</summary>
    RejectRecord,

    /// <summary>Unknown value resolves to the lookup's default value.</summary>
    UseDefault,

    /// <summary>Unknown value is added to the lookup list and the new id is used.</summary>
    AutoCreate,
}

public sealed class LookupSettings
{
    /// <summary>Name of the lookup list (for the built-in provider: the code list name).</summary>
    public required string List { get; set; }

    /// <summary>Key of the registered lookup provider. "codelist" is built in.</summary>
    public string Provider { get; set; } = "codelist";

    public LookupMissingPolicy OnMissing { get; set; } = LookupMissingPolicy.RejectRecord;

    public bool CaseInsensitive { get; set; } = true;

    /// <summary>
    /// Type of the value the provider resolves to, which is also the target column type.
    /// The built-in code-list provider returns int ids (the default); custom providers
    /// may resolve to strings, guids, etc.
    /// </summary>
    public FieldKind ValueKind { get; set; } = FieldKind.Int32;

    /// <summary>Raw value resolved instead when <see cref="OnMissing"/> is UseDefault.</summary>
    public string? Default { get; set; }
}
