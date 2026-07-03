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

    [Test]
    public void Decimal_fields_accept_exponent_notation_from_json()
    {
        var field = new FieldDefinition { Name = "f", Type = FieldKind.Decimal };

        Assert.That(Loadstone.Records.ValueConverter.TryConvert(field, "1.5e3", out var value, out _), Is.True);
        Assert.That(value, Is.EqualTo(1500m));
    }

    [Test]
    public void Out_of_range_precision_scale_and_maxlength_fail_validation()
    {
        var manifest = TestData.Orders();
        manifest.Root.Fields.Add(new FieldDefinition { Name = "Bad1", Type = FieldKind.Decimal, Precision = 5, Scale = 10 });
        manifest.Root.Fields.Add(new FieldDefinition { Name = "Bad2", Type = FieldKind.Decimal, Precision = 40 });
        manifest.Root.Fields.Add(new FieldDefinition { Name = "Bad3", MaxLength = 5000 });

        var errors = manifest.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("scale"));
            Assert.That(errors, Has.Some.Contains("precision"));
            Assert.That(errors, Has.Some.Contains("maxLength"));
        });
    }

    [Test]
    public void Oversized_natural_key_string_fails_validation()
    {
        var manifest = TestData.Orders();
        manifest.Root.Fields[0].MaxLength = 1000;

        Assert.That(manifest.Validate(), Has.Some.Contains("850"));
    }

    [Test]
    public async Task Xml_root_element_setting_scopes_matching_to_the_wrapper()
    {
        var manifest = TestData.Orders();
        manifest.Source.Xml.RootElement = "Body";
        const string xml = """
            <Envelope>
              <Preview><Order><OrderNumber>OUTSIDE</OrderNumber></Order></Preview>
              <Body><Order><OrderNumber>INSIDE</OrderNumber></Order></Body>
            </Envelope>
            """;

        var reader = new Loadstone.Readers.Xml.XmlSourceReader();
        var records = new List<Loadstone.Records.DataRecord>();
        await foreach (var record in reader.ReadAsync(TestData.AsStream(xml), manifest))
        {
            records.Add(record);
        }

        Assert.That(records.Single().Raw["OrderNumber"], Is.EqualTo("INSIDE"));
    }

    [Test]
    public void Xml_field_with_nested_markup_reports_a_source_format_error()
    {
        const string xml = """
            <Orders><Order><OrderNumber><b>A-1</b></OrderNumber></Order></Orders>
            """;

        var reader = new Loadstone.Readers.Xml.XmlSourceReader();
        Assert.ThrowsAsync<SourceFormatException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync(TestData.AsStream(xml), TestData.Orders()))
            {
            }
        });
    }

    [Test]
    public async Task Duplicate_csv_keys_reject_the_affected_parent()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "Order.csv", "_key,OrderNumber\nk1,A-1\nk1,A-2\n");
            AddEntry(archive, "Line.csv", "_key,_parentKey,LineNumber\nl1,k1,1\n");
        }

        zipStream.Position = 0;
        var reader = new Loadstone.Readers.Csv.CsvSourceReader();
        var records = new List<Loadstone.Records.DataRecord>();
        await foreach (var record in reader.ReadAsync(zipStream, TestData.Orders()))
        {
            records.Add(record);
        }

        Assert.That(records.Count(r => r.TreeHasErrors), Is.EqualTo(1), "the duplicate-key parent must carry an error");
        Assert.That(records.Count(r => !r.TreeHasErrors), Is.EqualTo(1));
    }

    private static void AddEntry(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
