using Loadstone.Manifests;
using Loadstone.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Loadstone.Tests;

[TestFixture]
public class MergeSqlTests
{
    [Test]
    public void Root_with_natural_key_merges_on_key_and_updates_data_columns()
    {
        var entity = TestData.Orders().Root;
        var sql = SqlServerImportWriter.BuildMergeSql(entity, "#stg", "#out", hasParent: false);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("MERGE [dbo].[Orders] WITH (HOLDLOCK)"));
            // OrderNumber is required, so the merge uses seekable plain equality.
            Assert.That(sql, Does.Contain("t.[OrderNumber] = s.[OrderNumber]"));
            Assert.That(sql, Does.Not.Contain("OrderNumber] IS NULL"));
            Assert.That(sql, Does.Contain("WHEN MATCHED THEN UPDATE SET"));
            Assert.That(sql, Does.Contain("t.[Total] = s.[Total]"));
            Assert.That(sql, Does.Not.Contain("t.[OrderNumber] = s.[OrderNumber],"), "key columns must not be updated");
            Assert.That(sql, Does.Contain("OUTPUT $action, inserted.[OrderId], s._StageId INTO #out"));
        });
    }

    [Test]
    public void Child_merge_scopes_natural_key_to_the_parent()
    {
        var entity = TestData.Orders().Root.Children[0];
        var sql = SqlServerImportWriter.BuildMergeSql(entity, "#stg", "#out", hasParent: true);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("t.[OrderId] = s._ParentKey"));
            Assert.That(sql, Does.Contain("INSERT ([OrderId], [LineNumber], [Sku], [Quantity])"));
            Assert.That(sql, Does.Contain("VALUES (s._ParentKey, s.[LineNumber], s.[Sku], s.[Quantity])"));
        });
    }

    [Test]
    public void Optional_natural_key_columns_keep_null_safe_comparison()
    {
        var entity = TestData.Orders().Root;
        entity.NaturalKey = ["Total"];
        var sql = SqlServerImportWriter.BuildMergeSql(entity, "#stg", "#out", hasParent: false);

        Assert.That(sql, Does.Contain("(t.[Total] = s.[Total] OR (t.[Total] IS NULL AND s.[Total] IS NULL))"));
    }

    [Test]
    public void Entity_without_natural_key_always_inserts()
    {
        var entity = TestData.Orders().Root;
        entity.NaturalKey.Clear();
        var sql = SqlServerImportWriter.BuildMergeSql(entity, "#stg", "#out", hasParent: false);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("ON 1 = 0"));
            Assert.That(sql, Does.Not.Contain("WHEN MATCHED"));
        });
    }

    [Test]
    public void Staging_tables_type_columns_from_the_manifest()
    {
        var ddl = SqlServerImportWriter.BuildStagingDdl(TestData.Orders().Root, "#stg", "#out", hasParent: false);

        Assert.Multiple(() =>
        {
            Assert.That(ddl, Does.Contain("[OrderNumber] nvarchar(20) NULL"));
            Assert.That(ddl, Does.Contain("[OrderDate] date NULL"));
            Assert.That(ddl, Does.Contain("[Country] int NULL"), "lookup columns hold the resolved id");
            Assert.That(ddl, Does.Contain("[Total] decimal(18,6) NULL"));
            Assert.That(ddl, Does.Contain("_StageId int NOT NULL"));
        });
    }
}

[TestFixture]
public class SchemaScriptTests
{
    private static string Script()
    {
        var manager = new SqlServerSchemaManager(null!, NullLogger<SqlServerSchemaManager>.Instance);
        return manager.GenerateTargetSchemaScript(TestData.Orders());
    }

    [Test]
    public void Generates_identity_primary_keys()
    {
        Assert.That(Script(), Does.Contain("[OrderId] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_dbo_Orders] PRIMARY KEY"));
    }

    [Test]
    public void Child_tables_get_a_foreign_key_to_the_parent()
    {
        Assert.That(Script(), Does.Contain("REFERENCES [dbo].[Orders] ([OrderId])"));
    }

    [Test]
    public void Natural_keys_become_unique_indexes_scoped_by_parent()
    {
        var script = Script();
        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("CREATE UNIQUE INDEX [UX_dbo_Orders_natural] ON [dbo].[Orders] ([OrderNumber])"));
            Assert.That(script, Does.Contain("CREATE UNIQUE INDEX [UX_dbo_OrderLines_natural] ON [dbo].[OrderLines] ([OrderId], [LineNumber])"));
        });
    }

    [Test]
    public void Script_is_guarded_for_reruns()
    {
        Assert.That(Script(), Does.Contain("IF OBJECT_ID(N'[dbo].[Orders]') IS NULL"));
    }

    [Test]
    public void Required_fields_are_not_nullable()
    {
        Assert.That(Script(), Does.Contain("[OrderNumber] nvarchar(20) NOT NULL"));
    }
}

[TestFixture]
public class WriterTransactionScopeTests
{
    [Test]
    public void Natural_keyed_dataset_without_reject_file_lookups_takes_batch_scope()
    {
        Assert.That(SqlServerImportWriter.RequiresJobScopedTransaction(TestData.Orders()), Is.False);
    }

    [Test]
    public void Entity_without_natural_key_forces_the_job_transaction()
    {
        var manifest = TestData.Orders();
        manifest.Root.Children[0].NaturalKey.Clear();

        Assert.That(SqlServerImportWriter.RequiresJobScopedTransaction(manifest), Is.True);
    }

    [Test]
    public void Reject_file_lookup_forces_the_job_transaction()
    {
        // rejectFile promises the whole file fails with nothing persisted; per-batch commits
        // would leave every batch before the offending record in the target tables.
        var manifest = TestData.Orders();
        manifest.Root.Fields.First(f => f.Lookup is not null).Lookup!.OnMissing = LookupMissingPolicy.RejectFile;

        Assert.That(SqlServerImportWriter.RequiresJobScopedTransaction(manifest), Is.True);
    }
}
