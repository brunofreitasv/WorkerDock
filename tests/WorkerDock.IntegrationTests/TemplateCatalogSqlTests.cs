using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.SqlServer;
using WorkerDock.Templates.Http;
namespace WorkerDock.IntegrationTests;

[Trait("Store","Sql")]
public sealed class TemplateCatalogSqlTests
{
    [Fact]
    public async Task Built_in_catalog_registers_only_v11_descriptors_after_migration()
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(false);
        await fixture.Runner.ApplyAsync("tenant", "0002_template_catalog_registration", default);
        var descriptors = new WorkerDock.Contracts.Templates.TemplateDescriptor[]
        {
            SqlTemplateDescriptors.Get("1.1.0"), HttpTemplateDescriptors.Get("1.1.0")
        };
        var catalog = new SqlTemplateCatalog(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId), descriptors);
        var aggregate = await catalog.RegisterAsync(descriptors, default);
        Assert.Equal(TemplateMetadata.AggregateDigest(descriptors), aggregate);
        Assert.Equal(2, (await catalog.ListAsync(default)).Count);
        Assert.Null(await catalog.GetAsync("workerdock.sql-server", "1.0.0", default));
        Assert.Null(await catalog.GetAsync("workerdock.http", "1.0.0", default));
        Assert.NotNull(await catalog.GetAsync("workerdock.sql-server", "1.1.0", default));
        Assert.NotNull(await catalog.GetAsync("workerdock.http", "1.1.0", default));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_v1_catalog_metadata_is_immutable_and_preserved(bool snapshot)
    {
        await using var fixture=new TenantSqlFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant","0001_initial",default);
        var original=TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor() with { Id="custom.http" });
        var changed=TemplateMetadata.WithDigest(original with { Description="changed" });
        var options=new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString,fixture.Database.InstanceId);
        var catalog=new SqlTemplateCatalog(options,[original,changed]);
        var job=new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(),0,"Pinned custom","custom.http","1.0.0",System.Text.Json.JsonSerializer.SerializeToElement(new { name="custom" }),null,AttemptLifecycleTests.Policy());
        await catalog.RegisterAsync([original],default);
        await new WorkerDock.Infrastructure.Jobs.SqlJobCommands(options).SaveAsync(job,null,new("synthetic","Anonymous",Guid.NewGuid()),default);
        Assert.Equal(original.MetadataDigest,(await catalog.GetAsync(original.Id,original.Version,default))!.MetadataDigest);
        Assert.Equal(3,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplateCatalog"));
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].JobRevisions WHERE TemplateId=N'custom.http' AND TemplateVersion=N'1.0.0'"));
        Assert.Equal(0,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Jobs WHERE Enabled=1"));
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplateCatalog WHERE TemplateId=N'custom.http' AND ConcurrencyLimit=1"));
        var conflict=await Assert.ThrowsAsync<WorkerDockException>(async()=>await catalog.RegisterAsync([changed],default));
        Assert.Equal("MetadataVersionConflict",conflict.Code);
        await catalog.RetireAsync(original.Id,original.Version,default); await catalog.RegisterAsync([original],default);
        Assert.False(await catalog.IsSelectableAsync(original.Id,original.Version,default));
        Assert.NotNull(await catalog.GetAsync(original.Id,original.Version,default));
        Assert.Single(await catalog.ListAsync(default));
        await Assert.ThrowsAsync<SqlException>(async()=>await fixture.Database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=REPLICATE('A',64) WHERE TemplateId=N'custom.http'"));
        await Assert.ThrowsAsync<SqlException>(async()=>await fixture.Database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=LOWER(MetadataDigest) WHERE TemplateId=N'custom.http'"));
        var rollback=await Assert.ThrowsAsync<WorkerDockException>(async()=>await fixture.Runner.RollbackAsync("tenant","0001_initial",default));
        Assert.Equal("UnsafeRollback",rollback.Code);
    }
    [Fact]
    public async Task Unrecognized_bootstrap_digest_cannot_be_promoted()
    {
        await using var database=new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.RuntimePrincipal,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0001_initial",default);
        await database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=REPLICATE('A',64) WHERE TemplateId=N'workerdock.http'");
        await runner.ApplyAsync("tenant","0001_initial",default);
        var descriptor=TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor() with { Id="workerdock.http" });
        var catalog=new SqlTemplateCatalog(new(database.ConnectionString,database.InstanceId),[descriptor]);
        var conflict=await Assert.ThrowsAsync<WorkerDockException>(async()=>await catalog.RegisterAsync([descriptor],default));
        Assert.Equal("MetadataVersionConflict",conflict.Code);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_templates_migration_supports_up_down_up(bool snapshot)
    {
        await using var database=new SqlDatabaseFixture(); await database.CreateAsync(snapshot);
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.RuntimePrincipal,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0001_initial",default);
        await runner.RollbackAsync("tenant","0001_initial",default);
        await runner.ApplyAsync("tenant","0001_initial",default);
        Assert.Equal(1,await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
    }
}
