using Loadstone.Manifests;
using Loadstone.Records;
using Loadstone.SqlServer;

namespace Loadstone.Tests;

[TestFixture]
public class FlatMergeSqlTests
{
    private static EntityDefinition Flat()
    {
        var entity = TestData.Orders().Root;
        entity.Children.Clear();
        return entity;
    }

    [Test]
    public void Flat_merge_has_no_staging_ids_and_counts_actions_only()
    {
        var sql = SqlServerImportWriter.BuildFlatMergeSql(Flat(), "#stg", "#out");

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("MERGE [dbo].[Orders] WITH (HOLDLOCK)"));
            Assert.That(sql, Does.Contain("t.[OrderNumber] = s.[OrderNumber]"));
            Assert.That(sql, Does.Contain("OUTPUT $action INTO #out (_Action)"));
            Assert.That(sql, Does.Not.Contain("_StageId"));
            Assert.That(sql, Does.Not.Contain("_ParentKey"));
        });
    }
}

[TestFixture]
public class DataRecordReaderTests
{
    private static async IAsyncEnumerable<DataRecord> Records(EntityDefinition entity)
    {
        var first = new DataRecord { Entity = entity };
        first.Values["OrderNumber"] = "A-1";
        first.Values["Total"] = 10.5m;
        yield return first;

        var second = new DataRecord { Entity = entity };
        second.Values["OrderNumber"] = "A-2";
        // Total left unset: must surface as DBNull.
        yield return second;
        await Task.CompletedTask;
    }

    [Test]
    public async Task Streams_values_in_field_order_with_dbnull_for_missing()
    {
        var entity = TestData.Orders().Root;
        entity.Children.Clear();
        using var reader = new DataRecordReader(Records(entity), entity, CancellationToken.None);

        Assert.That(reader.FieldCount, Is.EqualTo(entity.Fields.Count));
        Assert.That(reader.GetOrdinal("Total"), Is.EqualTo(3));

        Assert.That(await reader.ReadAsync(CancellationToken.None), Is.True);
        Assert.That(reader.GetValue(reader.GetOrdinal("OrderNumber")), Is.EqualTo("A-1"));
        Assert.That(reader.GetValue(reader.GetOrdinal("Total")), Is.EqualTo(10.5m));

        Assert.That(await reader.ReadAsync(CancellationToken.None), Is.True);
        Assert.That(reader.IsDBNull(reader.GetOrdinal("Total")), Is.True);

        Assert.That(await reader.ReadAsync(CancellationToken.None), Is.False);
        Assert.That(reader.RowsRead, Is.EqualTo(2));
    }
}
