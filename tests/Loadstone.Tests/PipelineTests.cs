using Loadstone.Jobs;
using Loadstone.Lookups;
using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Records;
using Loadstone.Runtime;
using Loadstone.Runtime.Datasets;
using Loadstone.Runtime.Lookups;
using Loadstone.Runtime.Steps;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Loadstone.Tests;

[TestFixture]
public class ValidateStepTests
{
    private static DataRecord OrderRecord(Action<DataRecord>? mutate = null)
    {
        var manifest = TestData.Orders();
        var record = new DataRecord { Entity = manifest.Root };
        record.Raw["OrderNumber"] = "A-1";
        record.Raw["Total"] = "10.5";
        mutate?.Invoke(record);
        return record;
    }

    private static ImportContext Context() => new()
    {
        Job = new ImportJob { Dataset = "orders", Queue = "orders", FileName = "f", FileReference = "r", Format = "xml" },
        Manifest = TestData.Orders(),
    };

    [Test]
    public async Task Converts_values_and_reports_no_issues()
    {
        var record = OrderRecord();
        await new ValidateStep().ExecuteAsync(Context(), record);

        Assert.Multiple(() =>
        {
            Assert.That(record.HasErrors, Is.False);
            Assert.That(record.Values["OrderNumber"], Is.EqualTo("A-1"));
            Assert.That(record.Values["Total"], Is.EqualTo(10.5m));
        });
    }

    [Test]
    public async Task Missing_required_field_is_an_error()
    {
        var record = OrderRecord(r => r.Raw.Remove("OrderNumber"));
        await new ValidateStep().ExecuteAsync(Context(), record);

        Assert.That(record.Issues.Single().Field, Is.EqualTo("OrderNumber"));
    }

    [Test]
    public async Task Bad_value_is_an_error_with_raw_value_preserved()
    {
        var record = OrderRecord(r => r.Raw["Total"] = "banana");
        await new ValidateStep().ExecuteAsync(Context(), record);

        var issue = record.Issues.Single();
        Assert.Multiple(() =>
        {
            Assert.That(issue.Field, Is.EqualTo("Total"));
            Assert.That(issue.RawValue, Is.EqualTo("banana"));
        });
    }

    [Test]
    public async Task Too_long_value_is_an_error()
    {
        var record = OrderRecord(r => r.Raw["OrderNumber"] = new string('x', 21));
        await new ValidateStep().ExecuteAsync(Context(), record);

        Assert.That(record.Issues.Single().Message, Does.Contain("maximum"));
    }

    [Test]
    public async Task Decimal_integer_overflow_is_a_row_error_not_a_job_abort()
    {
        // Total is decimal(18,6): 12 integer digits fit, 13 must be rejected here — if the
        // value reaches SQL it raises arithmetic overflow and aborts the whole import.
        var fits = OrderRecord(r => r.Raw["Total"] = "999999999999.99");
        var overflows = OrderRecord(r => r.Raw["Total"] = "1234567890123.45");

        await new ValidateStep().ExecuteAsync(Context(), fits);
        await new ValidateStep().ExecuteAsync(Context(), overflows);

        Assert.Multiple(() =>
        {
            Assert.That(fits.HasErrors, Is.False);
            Assert.That(overflows.HasErrors, Is.True);
            Assert.That(overflows.Issues.Single().Field, Is.EqualTo("Total"));
            Assert.That(overflows.Issues.Single().Message, Does.Contain("decimal(18,6)"));
        });
    }

    [Test]
    public async Task Decimal_that_rounds_into_an_extra_integer_digit_is_rejected()
    {
        // decimal(4,2) holds at most 99.99. "99.999" truncates to 2 integer digits but SQL
        // ROUNDS to scale on insert — 100.00 — which overflows and would abort the import.
        var manifest = TestData.Orders();
        var total = manifest.Root.Fields.First(f => f.Name == "Total");
        total.Precision = 4;
        total.Scale = 2;

        var record = new DataRecord { Entity = manifest.Root };
        record.Raw["OrderNumber"] = "A-1";
        record.Raw["Total"] = "99.999";
        var fits = new DataRecord { Entity = manifest.Root };
        fits.Raw["OrderNumber"] = "A-2";
        fits.Raw["Total"] = "99.994";   // rounds to 99.99 — still fits

        await new ValidateStep().ExecuteAsync(Context(), record);
        await new ValidateStep().ExecuteAsync(Context(), fits);

        Assert.Multiple(() =>
        {
            Assert.That(record.HasErrors, Is.True, "99.999 rounds to 100.00 which overflows decimal(4,2)");
            Assert.That(record.Issues.Single().Field, Is.EqualTo("Total"));
            Assert.That(fits.HasErrors, Is.False, "99.994 rounds to 99.99 which fits");
        });
    }

    [Test]
    public async Task Natural_key_string_without_maxlength_is_checked_against_the_indexable_default()
    {
        // A string natural-key field with no maxLength gets an nvarchar(400) column; a longer
        // value must fail validation here, not truncate/abort at merge time.
        var manifest = TestData.Orders();
        manifest.Root.Fields[0].MaxLength = null;   // OrderNumber, the natural key
        var record = new DataRecord { Entity = manifest.Root };
        record.Raw["OrderNumber"] = new string('x', FieldDefinition.IndexableStringDefaultLength + 1);
        record.Raw["Total"] = "1";

        await new ValidateStep().ExecuteAsync(Context(), record);

        Assert.Multiple(() =>
        {
            Assert.That(record.HasErrors, Is.True);
            Assert.That(record.Issues.Single().Field, Is.EqualTo("OrderNumber"));
            Assert.That(record.Issues.Single().Message, Does.Contain($"{FieldDefinition.IndexableStringDefaultLength}"));
        });
    }

    [Test]
    public async Task Errors_in_children_reject_the_tree_but_not_the_parent_record_itself()
    {
        var record = OrderRecord(r =>
        {
            var child = new DataRecord { Entity = TestData.Orders().Root.Children[0] };
            child.Raw["LineNumber"] = "not-a-number";
            r.Children.Add(child);
        });

        // Child entity instance differs from the context manifest's — validate uses the record's own entity.
        await new ValidateStep().ExecuteAsync(Context(), record);

        Assert.Multiple(() =>
        {
            Assert.That(record.HasErrors, Is.False);
            Assert.That(record.TreeHasErrors, Is.True);
        });
    }
}

[TestFixture]
public class LookupResolverTests
{
    private static CachingLookupResolver Resolver(FakeLookupProvider provider) => new(
        [provider],
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new LoadstoneOptions()),
        NullLogger<CachingLookupResolver>.Instance);

    private static FieldDefinition CountryField(LookupMissingPolicy policy, string? fallback = null) => new()
    {
        Name = "Country",
        Lookup = new LookupSettings { List = "countries", OnMissing = policy, Default = fallback },
    };

    private static FakeLookupProvider Countries() => new(new()
    {
        ["countries"] = new() { ["DK"] = 1, ["SE"] = 2, ["??"] = 99 },
    });

    [Test]
    public async Task Resolves_known_value()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.RejectRecord), "DK");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.Resolved));
            Assert.That(outcome.Value, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Resolves_case_insensitively_by_default()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.RejectRecord), "dk");

        Assert.That(outcome.Value, Is.EqualTo(1));
    }

    [Test]
    public async Task Unknown_value_rejects_record_by_default()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.RejectRecord), "XX");

        Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.RecordRejected));
    }

    [Test]
    public async Task Unknown_value_can_reject_whole_file()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.RejectFile), "XX");

        Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.FileRejected));
    }

    [Test]
    public async Task Unknown_value_can_fall_back_to_default()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.UseDefault, "??"), "XX");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.Resolved));
            Assert.That(outcome.Value, Is.EqualTo(99));
        });
    }

    [Test]
    public async Task Unknown_value_can_auto_create()
    {
        var provider = Countries();
        provider.SupportsCreate = true;
        var outcome = await Resolver(provider).ResolveAsync(CountryField(LookupMissingPolicy.AutoCreate), "XX");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.Resolved));
            Assert.That(provider.Created, Is.EqualTo(new[] { "XX" }));
        });
    }

    [Test]
    public async Task Auto_create_on_unsupporting_provider_rejects_file()
    {
        var outcome = await Resolver(Countries()).ResolveAsync(CountryField(LookupMissingPolicy.AutoCreate), "XX");

        Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.FileRejected));
    }

    [Test]
    public async Task Unregistered_provider_rejects_file()
    {
        var field = new FieldDefinition
        {
            Name = "X",
            Lookup = new LookupSettings { List = "l", Provider = "no-such-provider" },
        };

        var outcome = await Resolver(Countries()).ResolveAsync(field, "value");

        Assert.That(outcome.Kind, Is.EqualTo(LookupOutcomeKind.FileRejected));
    }
}

[TestFixture]
public class ImportEngineTests
{
    private const string Xml = """
        <Orders>
          <Order>
            <OrderNumber>A-1</OrderNumber>
            <Country>DK</Country>
            <Total>10</Total>
            <Line><LineNumber>1</LineNumber><Sku>X</Sku></Line>
          </Order>
          <Order>
            <OrderNumber>A-2</OrderNumber>
            <Country>XX</Country>
          </Order>
          <Order>
            <OrderNumber>A-3</OrderNumber>
            <Total>banana</Total>
          </Order>
        </Orders>
        """;

    private static (ImportEngine Engine, CollectingWriter Writer, InMemoryJobStore Store, ImportJob Job) Build(
        string content,
        DatasetManifest? manifest = null)
    {
        var registry = new DatasetRegistry();
        registry.Register(manifest ?? TestData.Orders());

        var provider = new FakeLookupProvider(new() { ["countries"] = new() { ["DK"] = 1 } });
        var resolver = new CachingLookupResolver(
            [provider],
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new LoadstoneOptions()),
            NullLogger<CachingLookupResolver>.Instance);

        var writer = new CollectingWriter();
        var store = new InMemoryJobStore();
        var engine = new ImportEngine(
            registry,
            [new Loadstone.Readers.Xml.XmlSourceReader()],
            [new ValidateStep(), new LookupStep(resolver)],
            writer,
            store,
            new SingleFileStore(System.Text.Encoding.UTF8.GetBytes(content)),
            Options.Create(new LoadstoneOptions()),
            NullLogger<ImportEngine>.Instance);

        var job = new ImportJob
        {
            Dataset = "orders",
            Queue = "orders",
            FileName = "orders.xml",
            FileReference = "stored",
            Format = "xml",
        };

        return (engine, writer, store, job);
    }

    [Test]
    public async Task Good_records_are_written_and_bad_ones_are_rejected_with_details()
    {
        var (engine, writer, store, job) = Build(Xml);

        var outcome = await engine.RunAsync(job);

        Assert.Multiple(() =>
        {
            // A-1 and its line survive; A-2 (unknown country) and A-3 (bad decimal) are rejected.
            Assert.That(writer.Roots, Has.Count.EqualTo(1));
            Assert.That(writer.Roots[0].Raw["OrderNumber"], Is.EqualTo("A-1"));
            Assert.That(outcome.RecordsRead, Is.EqualTo(4));
            Assert.That(outcome.RecordsRejected, Is.EqualTo(2));
            Assert.That(store.Rejections, Has.Count.EqualTo(2));
            Assert.That(store.Rejections.Select(r => r.Field), Is.EquivalentTo(new[] { "Country", "Total" }));
            Assert.That(store.Rejections.All(r => r.SourceLine > 0), Is.True);
        });
    }

    [Test]
    public async Task Emits_start_and_complete_events()
    {
        var (engine, _, store, job) = Build(Xml);

        await engine.RunAsync(job);

        Assert.That(store.Events.Select(e => e.Stage), Is.EqualTo(new[] { "start", "complete" }));
    }

    [Test]
    public void Unknown_dataset_fails_fast()
    {
        var (engine, _, _, job) = Build(Xml);
        job.Dataset = "nope";

        Assert.ThrowsAsync<KeyNotFoundException>(() => engine.RunAsync(job));
    }

    [Test]
    public void Dataset_format_restrictions_are_enforced()
    {
        var manifest = TestData.Orders();
        manifest.Source.Formats = ["json"];
        var (engine, _, _, job) = Build(Xml, manifest);

        Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(job));
    }
}
