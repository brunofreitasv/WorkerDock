using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Logging;
using WorkerDock.Templates.SqlServer;
namespace WorkerDock.IntegrationTests;
public sealed partial class SqlTemplateTests
{
    private const string Literal="Olá 漢字 '; DROP TABLE forbidden;--";
    private static SqlJobConfiguration Configuration(SqlTemplateTargetFixture f,string kind="Query",bool transaction=true)=>
        new(f.Profile.ProfileId,f.Profile.AllowedCatalogs[0],kind,kind=="Query"?"INSERT [workerdock].SqlTemplateTarget(ExecutionKey,Amount,TextValue,OptionalValue) VALUES(@key,@amount,@text,@optional)":"[workerdock].InsertSqlTemplateTarget",
        [new("@key",SqlDbType.NVarChar,null,null,true,false,100,null,null),new("@amount",SqlDbType.Decimal,SqlTemplateTests.Json(12.34m),null,false,false,null,10,2),new("@text",SqlDbType.NVarChar,SqlTemplateTests.Json(Literal),null,false,false,400,null,null),new("@optional",SqlDbType.Int,null,null,false,true,null,null,null)],null,transaction);
    private static TemplateExecutionContext Context(SqlTemplateTargetFixture f,SqlJobConfiguration configuration,string key,DateTimeOffset? until=null,bool diagnostic=false,string templateVersion="1.1.0")=>
        new(new ExecutionDescriptor(Guid.NewGuid(),1,"workerdock.sql-server",templateVersion,key,SqlTemplateTests.Json(configuration),f.Profile.ProfileRevisionId,SqlTemplateTests.Json(f.Profile),JsonSerializer.SerializeToElement(new { sqlProfileDiagnostic=diagnostic }),until),f.Vault,new SafeExecutionLog(_=>{}));
    [Trait("Store","Sql")]
    [Theory]
    [InlineData("Query")]
    [InlineData("Procedure")]
    public async Task Typed_parameters_do_not_interpolate_sql(string mode)
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();var old=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
            var result=await new SqlTemplate().ExecuteAsync(Context(fixture,Configuration(fixture,mode),"typed-key"),default);Assert.Equal("Success",result.Outcome);
            await using var c=new SqlConnection(fixture.Database.ConnectionString);await c.OpenAsync();await using var command=c.CreateCommand();command.CommandText="SELECT Amount,TextValue,OptionalValue FROM [workerdock].SqlTemplateTarget WHERE ExecutionKey=N'typed-key'";
            await using var reader=await command.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());Assert.Equal(12.34m,reader.GetDecimal(0));Assert.Equal(Literal,reader.GetString(1));Assert.True(reader.IsDBNull(2));
            Assert.DoesNotContain(Literal,JsonSerializer.Serialize(result));Assert.DoesNotContain(fixture.Profile.Server,result.SafeSummary.GetRawText());
        }
        finally{CultureInfo.CurrentCulture=old;}
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Commit_ack_loss_is_unknown_not_safe_retry()
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();
        var result=await new LostCommitAcknowledgement().ExecuteAsync(Context(fixture,Configuration(fixture),"lost-commit"),default);
        Assert.True(result.EffectsMayHaveOccurred);Assert.Equal("SqlCommitUnknown",result.SafeCode);Assert.Equal("TransientFailure",result.Outcome);
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SqlTemplateTarget WHERE ExecutionKey=N'lost-commit'"));
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Confirmed_failure_rolls_back_but_cancellation_remains_conservative()
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();var config=Configuration(fixture,"Procedure") with { CommandText="[workerdock].FailSqlTemplateTarget" };
        var failure=await new SqlTemplate().ExecuteAsync(Context(fixture,config,"rollback"),default);Assert.Equal("PermanentFailure",failure.Outcome);Assert.False(failure.EffectsMayHaveOccurred);
        Assert.Equal(0,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SqlTemplateTarget"));
        var waiting=Configuration(fixture) with { CommandText="INSERT [workerdock].SqlTemplateTarget(ExecutionKey,Amount,TextValue,OptionalValue) VALUES(@key,@amount,@text,@optional); WAITFOR DELAY '00:00:20';",CommandTimeoutSeconds=0 };
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running=new SqlTemplate().ExecuteAsync(Context(fixture,waiting,"cancelled"),stop.Token).AsTask();
        var observed=false;var until=DateTimeOffset.UtcNow.AddSeconds(8);
        while(DateTimeOffset.UtcNow<until && !running.IsCompleted)
        {
            if(await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SqlTemplateTarget WITH (READUNCOMMITTED) WHERE ExecutionKey=N'cancelled'")>0){observed=true;break;}
            await Task.Delay(50);
        }
        stop.Cancel();var cancelled=await running;Assert.True(observed,"Target did not reach the cancellation checkpoint");
        Assert.Equal("SqlCancelled",cancelled.SafeCode);Assert.True(cancelled.EffectsMayHaveOccurred);
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Diagnostic_ignores_command_text_and_returns_safe_connectivity_code()
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();
        var result=await new SqlTemplate().ExecuteAsync(Context(fixture,Configuration(fixture),"diagnostic",DateTimeOffset.UtcNow.AddSeconds(30),true),default);
        Assert.Equal("SqlConnectivityConfirmed",result.SafeCode);Assert.False(result.EffectsMayHaveOccurred);
        Assert.Equal(0,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SqlTemplateTarget"));
        Assert.Equal("{}",result.SafeSummary.GetRawText());
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Dynamic_reference_date_binds_as_sql_date()
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();
        var config=new SqlJobConfiguration(fixture.Profile.ProfileId,fixture.Profile.AllowedCatalogs[0],"Query",
            "INSERT [workerdock].SqlTemplateTarget(ExecutionKey,ReferenceDate) VALUES(@key,@date)",
            [new("@key",SqlDbType.NVarChar,SqlTemplateTests.Json("reference-date"),null,false,false,100,null,null),
             new("@date",SqlDbType.Date,SqlTemplateTests.Json("2026-10-20"),null,false,false,null,null,null)],null,true);
        var result=await new SqlTemplate("1.1.0").ExecuteAsync(Context(fixture,config,"reference-date",templateVersion:"1.1.0"),default);
        Assert.Equal("Success",result.Outcome);
        await using var connection=new SqlConnection(fixture.Database.ConnectionString);await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="SELECT ReferenceDate FROM [workerdock].SqlTemplateTarget WHERE ExecutionKey=N'reference-date'";
        Assert.Equal(new DateTime(2026,10,20),await command.ExecuteScalarAsync());
    }
    private sealed class LostCommitAcknowledgement : SqlTemplate
    {
        protected override async Task CommitAsync(SqlTransaction transaction,CancellationToken ct)
        {await base.CommitAsync(transaction,ct);throw new IOException("Synthetic acknowledgement loss secret-sentinel");}
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Diagnostic_auth_catalog_failures_and_secret_rotation_remain_safe()
    {
        await using var fixture=new SqlTemplateTargetFixture();await fixture.CreateAsync();var context=Context(fixture,Configuration(fixture),"safe-diagnostic",DateTimeOffset.UtcNow.AddSeconds(30),true);
        var failure=await new SqlTemplate().ExecuteAsync(context with { Secrets=new WrongPasswordVault() },default);
        Assert.Equal("SqlAuthenticationFailed",failure.SafeCode);Assert.False(failure.EffectsMayHaveOccurred);Assert.DoesNotContain("secret-sentinel",JsonSerializer.Serialize(failure));
        var rotated=await new SqlTemplate().ExecuteAsync(context,default);Assert.Equal("SqlConnectivityConfirmed",rotated.SafeCode);
        var missing="WorkerDock_Missing_"+Guid.NewGuid().ToString("N");
        var config=Configuration(fixture) with { Catalog=missing };var profile=fixture.Profile with { AllowedCatalogs=[missing] };
        var denied=await new SqlTemplate().ExecuteAsync(context with { Execution=context.Execution with { Configuration=SqlTemplateTests.Json(config),ProfileConfiguration=SqlTemplateTests.Json(profile) } },default);
        Assert.Equal("SqlCatalogUnavailable",denied.SafeCode);Assert.False(denied.EffectsMayHaveOccurred);Assert.Equal("{}",denied.SafeSummary.GetRawText());
    }
    private sealed class WrongPasswordVault : WorkerDock.Contracts.Secrets.ISecretVault
    {
        public ValueTask<WorkerDock.Contracts.Secrets.SecretValue> ResolveAsync(WorkerDock.Contracts.Secrets.SecretReference reference,CancellationToken ct)=>ValueTask.FromResult(new WorkerDock.Contracts.Secrets.SecretValue("wrong-secret-sentinel"));
    }}
