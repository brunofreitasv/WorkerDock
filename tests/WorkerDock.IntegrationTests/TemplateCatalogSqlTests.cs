using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Infrastructure.Templates;
namespace WorkerDock.IntegrationTests;

[Trait("Store","Sql")]
public sealed class TemplateCatalogSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restricted_catalog_promotes_bootstrap_once_and_preserves_retired_metadata(bool snapshot)
    {
        await using var fixture=new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant","0004_templates",default);
        var original=TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor() with { Id="workerdock.http" });
        var changed=TemplateMetadata.WithDigest(original with { Description="changed" });
        var options=new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString,fixture.Database.InstanceId);
        var catalog=new SqlTemplateCatalog(options,[original,changed]);
        var job=new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(),0,"Pinned legacy","workerdock.http","1.0.0",System.Text.Json.JsonSerializer.SerializeToElement(new { name="legacy" }),null,AttemptLifecycleTests.Policy());
        await new WorkerDock.Infrastructure.Jobs.SqlJobCommands(options).SaveAsync(job,null,new("synthetic","Anonymous",Guid.NewGuid()),default);
        await catalog.RegisterAsync([original],default);
        Assert.Equal(original.MetadataDigest,(await catalog.GetAsync(original.Id,original.Version,default))!.MetadataDigest);
        Assert.Equal(2,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplateCatalog"));
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].JobRevisions WHERE TemplateId=N'workerdock.http' AND TemplateVersion=N'1.0.0'"));
        Assert.Equal(0,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Jobs WHERE Enabled=1"));
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplatePolicies WHERE TemplateId=N'workerdock.http' AND StartGateClosed=1"));
        var conflict=await Assert.ThrowsAsync<WorkerDockException>(async()=>await catalog.RegisterAsync([changed],default));
        Assert.Equal("MetadataVersionConflict",conflict.Code);
        await catalog.RetireAsync(original.Id,original.Version,default); await catalog.RegisterAsync([original],default);
        Assert.False(await catalog.IsSelectableAsync(original.Id,original.Version,default));
        Assert.NotNull(await catalog.GetAsync(original.Id,original.Version,default));
        Assert.Single(await catalog.ListAsync(default));
        await Assert.ThrowsAsync<SqlException>(async()=>await fixture.Database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=REPLICATE('A',64) WHERE TemplateId=N'workerdock.http'"));
        await Assert.ThrowsAsync<SqlException>(async()=>await fixture.Database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=LOWER(MetadataDigest) WHERE TemplateId=N'workerdock.http'"));
        var rollback=await Assert.ThrowsAsync<WorkerDockException>(async()=>await fixture.Runner.RollbackAsync("tenant","0004_templates",default));
        Assert.Equal("UnsafeRollback",rollback.Code);
    }
    [Fact]
    public async Task Unrecognized_bootstrap_digest_cannot_be_promoted()
    {
        await using var database=new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0003d_dispatch",default);
        await database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET MetadataDigest=REPLICATE('A',64) WHERE TemplateId=N'workerdock.http'");
        await runner.ApplyAsync("tenant","0004_templates",default);
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
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0004_templates",default);
        await runner.RollbackAsync("tenant","0004_templates",default);
        await runner.ApplyAsync("tenant","0004_templates",default);
        Assert.Equal(8,await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
    }
}
