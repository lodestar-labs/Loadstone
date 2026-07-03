using Loadstone.Readers.Csv;

namespace Loadstone.Tests;

[TestFixture]
public class CsvParserTests
{
    private static async Task<List<CsvParser.CsvRow>> Parse(string content, char delimiter = ',')
    {
        using var reader = new StringReader(content);
        var rows = new List<CsvParser.CsvRow>();
        await foreach (var row in CsvParser.ParseAsync(reader, delimiter))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Test]
    public async Task Parses_simple_rows()
    {
        var rows = await Parse("a,b,c\r\n1,2,3\n");

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0].Fields, Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(rows[1].Fields, Is.EqualTo(new[] { "1", "2", "3" }));
    }

    [Test]
    public async Task Handles_quoted_fields_with_delimiters_and_newlines()
    {
        var rows = await Parse("\"a,x\",\"line1\nline2\",plain\n");

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Fields[0], Is.EqualTo("a,x"));
        Assert.That(rows[0].Fields[1], Is.EqualTo("line1\nline2"));
        Assert.That(rows[0].Fields[2], Is.EqualTo("plain"));
    }

    [Test]
    public async Task Handles_escaped_quotes()
    {
        var rows = await Parse("\"say \"\"hi\"\"\",b\n");

        Assert.That(rows[0].Fields[0], Is.EqualTo("say \"hi\""));
        Assert.That(rows[0].Fields[1], Is.EqualTo("b"));
    }

    [Test]
    public async Task Preserves_empty_fields()
    {
        var rows = await Parse("a,,c\n,,\n");

        Assert.That(rows[0].Fields, Is.EqualTo(new[] { "a", "", "c" }));
        Assert.That(rows[1].Fields, Is.EqualTo(new[] { "", "", "" }));
    }

    [Test]
    public async Task Skips_blank_lines_and_handles_missing_trailing_newline()
    {
        var rows = await Parse("a,b\n\n\nc,d");

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[1].Fields, Is.EqualTo(new[] { "c", "d" }));
    }

    [Test]
    public async Task Reports_the_line_each_row_starts_on()
    {
        var rows = await Parse("h1,h2\n\"multi\nline\",x\nlast,row\n");

        Assert.That(rows.Select(r => r.LineNumber), Is.EqualTo(new long[] { 1, 2, 4 }));
    }

    [Test]
    public async Task Supports_alternative_delimiters()
    {
        var rows = await Parse("a;b;\"c;d\"\n", ';');

        Assert.That(rows[0].Fields, Is.EqualTo(new[] { "a", "b", "c;d" }));
    }
}
