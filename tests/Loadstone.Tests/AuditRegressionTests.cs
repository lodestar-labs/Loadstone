using Loadstone.Jobs;
using Loadstone.Manifests;
using Loadstone.Pipeline;
using Loadstone.Records;
using Loadstone.Runtime.Steps;
using Loadstone.SqlServer;
using Loadstone.Sources;

namespace Loadstone.Tests;

/// <summary>Regression tests for issues found during the pre-release audit.</summary>
[TestFixture]
public class AuditRegressionTests
{
    [Test]
    public async Task Required_lookup_field_is_flagged_even_when_lookup_default_exists()
    {
        // lookup.default substitutes for unknown values, not missing ones.
        var manifest = TestData.Orders();
        var country = manifest.Root.Fields.Single(f => f.Name == "Country");
        country.Required = true;
        country.Lookup!.OnMissing = LookupMissingPolicy.UseDefault;
        country.Lookup.Default = "??";

        var record = new DataRecord { Entity = manifest.Root };
        record.Raw["OrderNumber"] = "A-1";
        var context = new ImportContext
        {
            Job = new ImportJob { Dataset = "orders", Queue = "orders", FileName = "f", FileReference = "r", Format = "xml" },
            Manifest = manifest,
        };

        await new ValidateStep().ExecuteAsync(context, record);

        Assert.That(record.Issues.Select(i => i.Field), Does.Contain("Country"));
    }

    [Test]
    public void Hierarchical_zip_without_header_row_is_rejected_up_front()
    {
        var manifest = TestData.Orders();
        manifest.Source.Csv.HasHeaderRow = false;

        // Any zip-signature stream suffices; the guard fires before entries are read.
        var zipBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        var reader = new Loadstone.Readers.Csv.CsvSourceReader();

        Assert.ThrowsAsync<SourceFormatException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync(new MemoryStream(zipBytes), manifest))
            {
            }
        });
    }

    [Test]
    public void Lookup_value_kind_drives_staging_and_target_column_types()
    {
        var manifest = TestData.Orders();
        var country = manifest.Root.Fields.Single(f => f.Name == "Country");
        country.Lookup!.ValueKind = FieldKind.String;
        country.MaxLength = 10;

        var ddl = SqlServerImportWriter.BuildStagingDdl(manifest.Root, "#stg", "#out", hasParent: false);

        Assert.That(ddl, Does.Contain("[Country] nvarchar(10) NULL"));
    }

    [Test]
    public void Field_column_colliding_with_key_column_is_invalid()
    {
        var manifest = TestData.Orders();
        manifest.Root.Fields.Add(new FieldDefinition { Name = "OrderId" });

        Assert.That(manifest.Validate(), Has.Some.Contains("keyColumn"));
    }

    [Test]
    public void Field_column_colliding_with_parent_key_column_is_invalid()
    {
        var manifest = TestData.Orders();
        manifest.Root.Children[0].Fields.Add(new FieldDefinition { Name = "OrderId" });

        Assert.That(manifest.Validate(), Has.Some.Contains("parentKeyColumn"));
    }

    [Test]
    public void Lookup_value_kind_defaults_to_int()
    {
        var ddl = SqlServerImportWriter.BuildStagingDdl(TestData.Orders().Root, "#stg", "#out", hasParent: false);

        Assert.That(ddl, Does.Contain("[Country] int NULL"));
    }

    [Test]
    public void Qualified_table_names_escape_closing_brackets()
    {
        var entity = TestData.Orders().Root;
        entity.Table = "Ord]ers";

        Assert.That(entity.QualifiedTable, Is.EqualTo("[dbo].[Ord]]ers]"));
    }
}
