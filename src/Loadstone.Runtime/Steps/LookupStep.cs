using Loadstone.Lookups;
using Loadstone.Pipeline;
using Loadstone.Records;

namespace Loadstone.Runtime.Steps;

/// <summary>
/// Second built-in step: resolves every lookup field through the configured provider,
/// applying the field's missing-value policy (reject the record, substitute a default,
/// auto-create the entry, or abort the file).
/// </summary>
public sealed class LookupStep(ILookupResolver resolver) : IImportStep
{
    public string Name => "lookups";

    public int Order => 200;

    public bool AppliesTo(Manifests.DatasetManifest manifest) =>
        manifest.EnumerateEntities().Any(e => e.Fields.Any(f => f.Lookup is not null));

    public async ValueTask ExecuteAsync(ImportContext context, DataRecord root, CancellationToken cancellationToken = default)
    {
        foreach (var record in root.SelfAndDescendants())
        {
            foreach (var field in record.Entity.Fields)
            {
                if (field.Lookup is null)
                {
                    continue;
                }

                record.Raw.TryGetValue(field.Name, out var raw);
                raw ??= field.Default;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    record.Values[field.ColumnName] = null;
                    continue;
                }

                var outcome = await resolver.ResolveAsync(field, raw.Trim(), cancellationToken);
                switch (outcome.Kind)
                {
                    case LookupOutcomeKind.Resolved:
                        record.Values[field.ColumnName] = outcome.Value;
                        break;

                    case LookupOutcomeKind.RecordRejected:
                        record.AddError(field.Name, outcome.Message ?? "Unknown lookup value.", raw);
                        break;

                    case LookupOutcomeKind.FileRejected:
                        throw new LookupRejectedFileException(
                            outcome.Message ?? $"Unknown value '{raw}' for lookup '{field.Lookup.List}'.");
                }
            }
        }
    }
}
