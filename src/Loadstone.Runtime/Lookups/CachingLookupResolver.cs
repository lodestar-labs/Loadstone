using Loadstone.Lookups;
using Loadstone.Manifests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loadstone.Runtime.Lookups;

/// <summary>
/// Routes lookup fields to the registered provider, caches resolved values in memory, and
/// applies the manifest's missing-value policy.
/// </summary>
public sealed class CachingLookupResolver(
    IEnumerable<ILookupProvider> providers,
    IMemoryCache cache,
    IOptions<LoadstoneOptions> options,
    ILogger<CachingLookupResolver> logger) : ILookupResolver
{
    private readonly Dictionary<string, ILookupProvider> _providers =
        providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _cacheDuration = options.Value.LookupCacheDuration;

    public async ValueTask<LookupOutcome> ResolveAsync(FieldDefinition field, string rawValue, CancellationToken cancellationToken = default)
    {
        var lookup = field.Lookup!;
        if (!_providers.TryGetValue(lookup.Provider, out var provider))
        {
            return LookupOutcome.RejectFile(
                $"No lookup provider named '{lookup.Provider}' is registered (field '{field.Name}').");
        }

        var result = await ResolveCachedAsync(provider, lookup, rawValue, cancellationToken);
        if (result.Found)
        {
            return LookupOutcome.Resolved(result.Value);
        }

        switch (lookup.OnMissing)
        {
            case LookupMissingPolicy.UseDefault:
                var fallback = await ResolveCachedAsync(provider, lookup, lookup.Default!, cancellationToken);
                return fallback.Found
                    ? LookupOutcome.Resolved(fallback.Value)
                    : LookupOutcome.RejectRecord(
                        $"Value '{rawValue}' and default '{lookup.Default}' are both missing from lookup '{lookup.List}'.");

            case LookupMissingPolicy.AutoCreate:
                try
                {
                    var created = await provider.CreateAsync(lookup.List, rawValue, cancellationToken);
                    cache.Set(CacheKey(lookup, rawValue), LookupResult.Hit(created), _cacheDuration);
                    logger.LogInformation(
                        "Lookup {LookupList}: created missing entry {LookupValue}", lookup.List, rawValue);
                    return LookupOutcome.Resolved(created);
                }
                catch (NotSupportedException ex)
                {
                    return LookupOutcome.RejectFile(ex.Message);
                }

            case LookupMissingPolicy.RejectFile:
                return LookupOutcome.RejectFile(
                    $"Value '{rawValue}' is missing from lookup '{lookup.List}' and the dataset requires the whole file to be rejected.");

            default:
                return LookupOutcome.RejectRecord($"Value '{rawValue}' is missing from lookup '{lookup.List}'.");
        }
    }

    private async ValueTask<LookupResult> ResolveCachedAsync(
        ILookupProvider provider,
        LookupSettings lookup,
        string value,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(lookup, value);
        if (cache.TryGetValue(key, out LookupResult cached))
        {
            return cached;
        }

        var result = await provider.ResolveAsync(lookup.List, value, lookup.CaseInsensitive, cancellationToken);
        if (result.Found)
        {
            cache.Set(key, result, _cacheDuration);
        }

        return result;
    }

    private static string CacheKey(LookupSettings lookup, string value)
    {
        var normalized = lookup.CaseInsensitive ? value.ToUpperInvariant() : value;
        return $"loadstone:lookup:{lookup.Provider}:{lookup.List}:{normalized}";
    }
}
