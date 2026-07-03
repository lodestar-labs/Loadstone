using Loadstone.Manifests;
using Loadstone.Records;

namespace Loadstone.Tests;

[TestFixture]
public class ValueConverterTests
{
    private static object? Convert(FieldKind kind, string? raw, string? format = null)
    {
        var field = new FieldDefinition { Name = "f", Type = kind, Format = format };
        Assert.That(ValueConverter.TryConvert(field, raw, out var value, out var error), Is.True, error);
        return value;
    }

    [Test]
    public void Converts_primitives_with_invariant_culture()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Convert(FieldKind.Int32, "42"), Is.EqualTo(42));
            Assert.That(Convert(FieldKind.Int64, "9000000000"), Is.EqualTo(9_000_000_000L));
            Assert.That(Convert(FieldKind.Decimal, "123.45"), Is.EqualTo(123.45m));
            Assert.That(Convert(FieldKind.Double, "1.5e3"), Is.EqualTo(1500d));
            Assert.That(Convert(FieldKind.Guid, "6f9619ff-8b86-d011-b42d-00c04fc964ff"),
                Is.EqualTo(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")));
        });
    }

    [TestCase("true", true)]
    [TestCase("1", true)]
    [TestCase("Y", true)]
    [TestCase("yes", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("No", false)]
    public void Converts_boolean_tokens(string raw, bool expected)
    {
        Assert.That(Convert(FieldKind.Boolean, raw), Is.EqualTo(expected));
    }

    [Test]
    public void Converts_dates_with_and_without_format()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Convert(FieldKind.Date, "2026-01-15"), Is.EqualTo(new DateOnly(2026, 1, 15)));
            Assert.That(Convert(FieldKind.Date, "20260115", "yyyyMMdd"), Is.EqualTo(new DateOnly(2026, 1, 15)));
            Assert.That(Convert(FieldKind.DateTime, "2026-01-15T10:30:00"), Is.EqualTo(new DateTime(2026, 1, 15, 10, 30, 0)));
            Assert.That(Convert(FieldKind.Time, "10:30"), Is.EqualTo(new TimeOnly(10, 30)));
        });
    }

    [Test]
    public void Blank_becomes_null_and_default_applies()
    {
        var withDefault = new FieldDefinition { Name = "f", Type = FieldKind.Int32, Default = "7" };
        ValueConverter.TryConvert(withDefault, "  ", out var defaulted, out _);
        Assert.That(defaulted, Is.EqualTo(7));

        var noDefault = new FieldDefinition { Name = "f", Type = FieldKind.Int32 };
        ValueConverter.TryConvert(noDefault, null, out var value, out _);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void Parse_failure_reports_error_instead_of_throwing()
    {
        var field = new FieldDefinition { Name = "f", Type = FieldKind.Int32 };
        var ok = ValueConverter.TryConvert(field, "not-a-number", out var value, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Does.Contain("not-a-number"));
        });
    }

    [Test]
    public void Values_are_trimmed_before_parsing()
    {
        Assert.That(Convert(FieldKind.Int32, " 42 "), Is.EqualTo(42));
    }
}
