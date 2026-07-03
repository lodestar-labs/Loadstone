namespace Loadstone.Manifests;

/// <summary>
/// Compile-time dataset definition. Implementing types declare their manifest through a
/// static abstract member, so datasets can be registered as types
/// (<c>builder.AddDataset&lt;MyDataset&gt;()</c>) and the compiler guarantees a manifest exists —
/// no reflection, no runtime discovery.
/// </summary>
public interface IDataset
{
    static abstract DatasetManifest Manifest { get; }
}
