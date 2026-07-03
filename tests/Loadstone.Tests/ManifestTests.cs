using Loadstone.Manifests;

namespace Loadstone.Tests;

[TestFixture]
public class ManifestTests
{
    [Test]
    public void Valid_manifest_passes_validation()
    {
        Assert.That(TestData.Orders().Validate(), Is.Empty);
    }

    [Test]
    public void Serializer_round_trips_manifest()
    {
        var original = TestData.Orders();
        var json = ManifestSerializer.Serialize(original);
        var restored = ManifestSerializer.Deserialize(json);

        Assert.That(restored.Name, Is.EqualTo("orders"));
        Assert.That(restored.Root.Children, Has.Count.EqualTo(1));
        Assert.That(restored.Root.Fields[2].Lookup!.List, Is.EqualTo("countries"));
        Assert.That(restored.Root.Children[0].ParentKeyColumn, Is.EqualTo("OrderId"));
    }

    [Test]
    public void Child_without_parent_key_is_invalid()
    {
        var manifest = TestData.Orders();
        manifest.Root.Children[0].ParentKeyColumn = null;

        Assert.That(manifest.Validate(), Has.Some.Contains("parentKeyColumn"));
    }

    [Test]
    public void Duplicate_entity_names_are_invalid()
    {
        var manifest = TestData.Orders();
        manifest.Root.Children[0].Name = "Order";

        Assert.That(manifest.Validate(), Has.Some.Contains("unique"));
    }

    [Test]
    public void Natural_key_must_reference_a_field_column()
    {
        var manifest = TestData.Orders();
        manifest.Root.NaturalKey = ["DoesNotExist"];

        Assert.That(manifest.Validate(), Has.Some.Contains("DoesNotExist"));
    }

    [Test]
    public void UseDefault_lookup_requires_default_value()
    {
        var manifest = TestData.Orders();
        manifest.Root.Fields[2].Lookup!.OnMissing = LookupMissingPolicy.UseDefault;

        Assert.That(manifest.Validate(), Has.Some.Contains("UseDefault"));
    }

    [Test]
    public void Entities_enumerate_parents_before_children()
    {
        var names = TestData.Orders().EnumerateEntities().Select(e => e.Name).ToArray();

        Assert.That(names, Is.EqualTo(new[] { "Order", "Line" }));
    }

    [Test]
    public void Deserialize_rejects_invalid_manifest()
    {
        const string json = """{ "name": "broken", "root": { "name": "A", "table": "A", "fields": [] } }""";

        Assert.Throws<ManifestException>(() => ManifestSerializer.Deserialize(json));
    }
}
