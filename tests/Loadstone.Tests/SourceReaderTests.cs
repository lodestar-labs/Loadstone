using System.IO.Compression;
using System.Text;
using Loadstone.Manifests;
using Loadstone.Readers.Csv;
using Loadstone.Readers.Json;
using Loadstone.Readers.Xml;
using Loadstone.Records;
using Loadstone.Sources;

namespace Loadstone.Tests;

[TestFixture]
public class XmlSourceReaderTests
{
    private static async Task<List<DataRecord>> Read(string xml)
    {
        var reader = new XmlSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(TestData.AsStream(xml), TestData.Orders()))
        {
            records.Add(record);
        }

        return records;
    }

    [Test]
    public async Task Reads_hierarchical_records_with_children()
    {
        const string xml = """
            <Orders>
              <Order>
                <OrderNumber>A-1</OrderNumber>
                <OrderDate>2026-01-15</OrderDate>
                <Total>99.50</Total>
                <Line><LineNumber>1</LineNumber><Sku>X</Sku><Quantity>2</Quantity></Line>
                <Line><LineNumber>2</LineNumber><Sku>Y</Sku><Quantity>1</Quantity></Line>
              </Order>
              <Order>
                <OrderNumber>A-2</OrderNumber>
              </Order>
            </Orders>
            """;

        var records = await Read(xml);

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Raw["OrderNumber"], Is.EqualTo("A-1"));
        Assert.That(records[0].Children, Has.Count.EqualTo(2));
        Assert.That(records[0].Children[1].Raw["Sku"], Is.EqualTo("Y"));
        Assert.That(records[1].Children, Is.Empty);
    }

    [Test]
    public async Task Ignores_unknown_elements()
    {
        const string xml = """
            <Orders>
              <Metadata><Exported>today</Exported></Metadata>
              <Order>
                <OrderNumber>A-1</OrderNumber>
                <SomethingExtra><Deep>ignored</Deep></SomethingExtra>
                <Line><LineNumber>1</LineNumber></Line>
              </Order>
            </Orders>
            """;

        var records = await Read(xml);

        Assert.That(records, Has.Count.EqualTo(1));
        Assert.That(records[0].Raw, Does.Not.ContainKey("SomethingExtra"));
        Assert.That(records[0].Children, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Captures_source_line_numbers()
    {
        const string xml = "<Orders>\n  <Order>\n    <OrderNumber>A-1</OrderNumber>\n  </Order>\n</Orders>";

        var records = await Read(xml);

        Assert.That(records[0].Location.Line, Is.EqualTo(2));
        Assert.That(records[0].Location.Path, Is.EqualTo("Order"));
    }

    [Test]
    public async Task Reads_attribute_sourced_fields()
    {
        var manifest = TestData.Orders();
        manifest.Root.Fields[0].Source = FieldSource.Attribute;

        var reader = new XmlSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(
            TestData.AsStream("""<Orders><Order OrderNumber="A-9"><Total>1</Total></Order></Orders>"""), manifest))
        {
            records.Add(record);
        }

        Assert.That(records[0].Raw["OrderNumber"], Is.EqualTo("A-9"));
    }

    [Test]
    public void Malformed_xml_raises_source_format_error()
    {
        Assert.ThrowsAsync<SourceFormatException>(async () => await Read("<Orders><Order></Orders>"));
    }
}

[TestFixture]
public class JsonSourceReaderTests
{
    private static async Task<List<DataRecord>> Read(string json, DatasetManifest? manifest = null)
    {
        var reader = new JsonSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(TestData.AsStream(json), manifest ?? TestData.Orders()))
        {
            records.Add(record);
        }

        return records;
    }

    [Test]
    public async Task Reads_top_level_array()
    {
        const string json = """
            [
              { "orderNumber": "A-1", "total": 10.5, "Line": [ { "lineNumber": 1, "sku": "X" } ] },
              { "orderNumber": "A-2", "total": 3 }
            ]
            """;

        var records = await Read(json);

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Raw["OrderNumber"], Is.EqualTo("A-1"));
        Assert.That(records[0].Raw["Total"], Is.EqualTo("10.5"));
        Assert.That(records[0].Children.Single().Raw["Sku"], Is.EqualTo("X"));
    }

    [Test]
    public async Task Reads_wrapped_object_via_root_entity_name()
    {
        const string json = """{ "order": [ { "orderNumber": "A-1" } ] }""";

        var records = await Read(json);

        Assert.That(records.Single().Raw["OrderNumber"], Is.EqualTo("A-1"));
    }

    [Test]
    public async Task Single_child_object_becomes_one_child()
    {
        const string json = """[ { "orderNumber": "A-1", "line": { "lineNumber": 1 } } ]""";

        var records = await Read(json);

        Assert.That(records[0].Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scalar_top_level_is_rejected()
    {
        Assert.ThrowsAsync<SourceFormatException>(async () => await Read("42"));
    }
}

[TestFixture]
public class CsvSourceReaderTests
{
    [Test]
    public async Task Flat_dataset_reads_single_csv()
    {
        var manifest = TestData.Orders();
        manifest.Root.Children.Clear();

        var reader = new CsvSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(
            TestData.AsStream("OrderNumber,Total\nA-1,10.5\nA-2,3\n"), manifest))
        {
            records.Add(record);
        }

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Raw["OrderNumber"], Is.EqualTo("A-1"));
        Assert.That(records[1].Location.Line, Is.EqualTo(3));
    }

    [Test]
    public void Hierarchical_dataset_requires_zip()
    {
        var reader = new CsvSourceReader();

        Assert.ThrowsAsync<SourceFormatException>(async () =>
        {
            await foreach (var _ in reader.ReadAsync(TestData.AsStream("OrderNumber\nA-1\n"), TestData.Orders()))
            {
            }
        });
    }

    [Test]
    public async Task Zip_archive_links_children_to_parents()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "Order.csv", "_key,OrderNumber,Total\nk1,A-1,10\nk2,A-2,20\n");
            AddEntry(archive, "Line.csv", "_key,_parentKey,LineNumber,Sku\nl1,k1,1,X\nl2,k1,2,Y\nl3,k2,1,Z\n");
        }

        zipStream.Position = 0;
        var reader = new CsvSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(zipStream, TestData.Orders()))
        {
            records.Add(record);
        }

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Children, Has.Count.EqualTo(2));
        Assert.That(records[1].Children.Single().Raw["Sku"], Is.EqualTo("Z"));
    }

    [Test]
    public async Task Orphaned_child_rows_are_flagged_for_rejection()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "Order.csv", "_key,OrderNumber\nk1,A-1\n");
            AddEntry(archive, "Line.csv", "_key,_parentKey,LineNumber\nl1,missing,1\n");
        }

        zipStream.Position = 0;
        var reader = new CsvSourceReader();
        var records = new List<DataRecord>();
        await foreach (var record in reader.ReadAsync(zipStream, TestData.Orders()))
        {
            records.Add(record);
        }

        var orphanContainers = records.Where(r => r.TreeHasErrors).ToArray();
        Assert.That(orphanContainers, Has.Length.EqualTo(1));
        Assert.That(records.Where(r => !r.TreeHasErrors), Has.Exactly(1).Items);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
