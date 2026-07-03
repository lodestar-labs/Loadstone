namespace Loadstone.Runtime;

public sealed class LoadstoneOptions
{
    public const string SectionName = "Loadstone";

    /// <summary>Connection string for the target database and Loadstone's system tables.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Directory where registered dataset manifests are persisted as JSON.</summary>
    public string ManifestDirectory { get; set; } = "data/datasets";

    /// <summary>Directory (or mount) where uploaded files are stored until processed.</summary>
    public string FileStorePath { get; set; } = "data/imports";

    /// <summary>How often idle queue workers poll for new jobs.</summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long resolved lookup values stay cached in memory.</summary>
    public TimeSpan LookupCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Base delay for retrying failed jobs; doubles with each attempt.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Jobs stuck in Processing longer than this are assumed crashed and requeued.</summary>
    public TimeSpan AbandonedJobTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Root records per write batch (one transaction per batch).</summary>
    public int WriterBatchSize { get; set; } = 500;

    /// <summary>Delete the uploaded file from the file store after a successful import.</summary>
    public bool DeleteFilesAfterImport { get; set; }

    /// <summary>Create missing target tables from the manifest when a dataset is registered.</summary>
    public bool AutoCreateTargetTables { get; set; }
}
