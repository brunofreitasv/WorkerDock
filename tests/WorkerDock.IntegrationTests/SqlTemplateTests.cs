using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Connections;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Templates.SqlServer;
using WorkerDock.Infrastructure.Templates;
namespace WorkerDock.IntegrationTests;
public sealed partial class SqlTemplateTests
{
    internal static ConnectionProfileRevision Profile(Guid? id=null) => new(Guid.NewGuid(),id ?? Guid.NewGuid(),1,"localhost",["Target"],"Integrated",null,null,true,false) { Name="Target profile" };
    internal static SqlJobConfiguration Config(Guid id, params SqlParameterConfiguration[] parameters) => new(id,"Target","Query","SELECT @value",parameters,null,true);
    internal static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, SqlJobConfiguration.JsonOptions);
    [Fact]
    public async Task Typed_command_parameters_are_bound_without_interpolation()
    {
        var old=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
            const string injection="Olá 漢字 '; DROP TABLE victims;--";
            var config=Config(Guid.NewGuid(),new SqlParameterConfiguration("@value",SqlDbType.Decimal,Json(12.34m),null,false,false,null,10,2),new("@text",SqlDbType.NVarChar,Json(injection),null,false,false,200,null,null),new("@null",SqlDbType.Int,null,null,false,true,null,null,null));
            using var connection=new SqlConnection();
            using var command=await SqlTemplate.BuildCommandAsync(config,connection,new NoVault(),"execution-key",null,default);
            Assert.Equal(config.CommandText,command.CommandText);
            Assert.Equal(12.34m,command.Parameters["@value"].Value);
            Assert.Equal(injection,command.Parameters["@text"].Value);
            Assert.Equal(DBNull.Value,command.Parameters["@null"].Value);
        }
        finally { CultureInfo.CurrentCulture=old; }
    }
    [Fact]
    public async Task Catalog_rejection_precedes_secret_resolution()
    {
        var profile=Profile() with { Authentication="VaultPassword",UserName="worker",PasswordReference=new("password") };
        var error=await Assert.ThrowsAsync<SqlTemplateException>(async()=>await SqlConnectionBuilder.BuildAsync(profile,"Forbidden",new NoVault(),default));
        Assert.Equal("CatalogNotAllowed",error.SafeCode);
    }
    [Fact]
    public async Task Builder_quotes_values_and_enforces_explicit_TLS()
    {
        var profile=Profile() with { Server="server;Initial Catalog=evil" };
        await using var connection=await SqlConnectionBuilder.BuildAsync(profile,"Target",new NoVault(),default);
        var settings=new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(profile.Server,settings.DataSource); Assert.Equal("Target",settings.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory,settings.Encrypt); Assert.False(settings.TrustServerCertificate);
        Assert.True(settings.IntegratedSecurity); Assert.InRange(settings.ConnectTimeout,1,15); Assert.False(settings.PersistSecurityInfo);
    }
    [Fact]
    public void Installed_descriptor_is_valid_and_immutable()
    {
        var descriptor=new SqlTemplate().Descriptor;
        Assert.Equal("workerdock.sql-server",descriptor.Id); Assert.Equal("1.1.0",descriptor.Version);
        Assert.Empty(TemplateSchemaValidator.ValidateDescriptor(descriptor)); Assert.Equal(TemplateMetadata.ComputeDigest(descriptor),descriptor.MetadataDigest);
        Assert.NotEmpty(new SqlTemplate().Validate(Json(Config(Guid.NewGuid()) with { CommandKind="Procedure",CommandText="[workerdock].proc; SELECT 1" })));
        Assert.NotEmpty(new SqlTemplate().Validate(Json(Config(Guid.NewGuid(),new SqlParameterConfiguration("@value",SqlDbType.Int,Json(1),new("secret"),false,false,null,null,null)))));
    }
    [Fact]
    public async Task Timeouts_inherit_deadline_and_zero_requires_infinite_outer_policy()
    {
        using var c=new SqlConnection(); var config=Config(Guid.NewGuid());
        using var inherited=await SqlTemplate.BuildCommandAsync(config,c,new NoVault(),"key",DateTimeOffset.UtcNow.AddSeconds(12),default);
        Assert.InRange(inherited.CommandTimeout,1,12);
        using var infinite=await SqlTemplate.BuildCommandAsync(config with { CommandTimeoutSeconds=0 },c,new NoVault(),"key",null,default);
        Assert.Equal(0,infinite.CommandTimeout);
        await Assert.ThrowsAsync<SqlTemplateException>(async()=>await SqlTemplate.BuildCommandAsync(config with { CommandTimeoutSeconds=0 },c,new NoVault(),"key",DateTimeOffset.UtcNow.AddMinutes(1),default));
    }
    [Fact]
    public void Deadline_cancellation_preserves_deadlines_longer_than_timer_limit()
    {
        var clock=new ManualTimeProvider();var deadline=clock.GetUtcNow().AddDays(60);using var cancellation=new CancellationTokenSource();
        using var schedule=SqlDeadlineCancellation.Schedule(cancellation,deadline,clock);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.True(clock.NextDueTime<=TimeSpan.FromMilliseconds(int.MaxValue-1));
        clock.Advance(clock.NextDueTime);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.True(clock.NextDueTime<=TimeSpan.FromMilliseconds(int.MaxValue-1));
        clock.Advance(deadline-clock.GetUtcNow());
        Assert.True(cancellation.IsCancellationRequested);
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now=new(2026,1,1,0,0,0,TimeSpan.Zero);
        private ManualTimer? timer;
        public override DateTimeOffset GetUtcNow()=>now;
        public TimeSpan NextDueTime=>timer?.DueTime ?? Timeout.InfiniteTimeSpan;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {timer=new ManualTimer(callback,state);timer.Change(dueTime,period);return timer;}
        public void Advance(TimeSpan duration){now+=duration;timer?.FireIfDue();}
        private sealed class ManualTimer(TimerCallback callback,object? state) : ITimer
        {
            public TimeSpan DueTime {get;private set;}=Timeout.InfiniteTimeSpan;
            public bool Change(TimeSpan dueTime,TimeSpan period){DueTime=dueTime;return true;}
            public void FireIfDue(){if(DueTime==Timeout.InfiniteTimeSpan)return;DueTime=Timeout.InfiniteTimeSpan;callback(state);}
            public void Dispose()=>DueTime=Timeout.InfiniteTimeSpan;
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    internal sealed class NoVault : ISecretVault { public ValueTask<SecretValue> ResolveAsync(SecretReference reference,CancellationToken ct)=>throw new InvalidOperationException("Unexpected secret resolution"); }
    [Fact]
    public async Task Missing_or_mismatched_profile_and_provider_failure_are_safe_before_effects()
    {
        var profile=Profile() with { Authentication="VaultPassword",UserName="worker",PasswordReference=new("password") };
        var descriptor=new WorkerDock.Contracts.Execution.ExecutionDescriptor(Guid.NewGuid(),1,"workerdock.sql-server","1.1.0","key",Json(Config(profile.ProfileId)),Guid.NewGuid(),Json(profile),Json(new {}),null);
        var context=new WorkerDock.Contracts.Templates.TemplateExecutionContext(descriptor,new SentinelVault(),new WorkerDock.Infrastructure.Logging.SafeExecutionLog(_=>{}));
        var mismatch=await new SqlTemplate().ExecuteAsync(context,default);Assert.Equal("ProfileRevisionMismatch",mismatch.SafeCode);Assert.False(mismatch.EffectsMayHaveOccurred);
        var unavailable=await new SqlTemplate().ExecuteAsync(context with { Execution=descriptor with { ProfileRevisionId=profile.ProfileRevisionId } },default);
        Assert.False(unavailable.EffectsMayHaveOccurred);Assert.DoesNotContain("secret-sentinel",JsonSerializer.Serialize(unavailable));Assert.Equal("PermanentFailure",unavailable.Outcome);
    }
    [Fact]
    public async Task Vault_credential_rotation_occurs_at_use_and_literals_never_enter_summary()
    {
        var profile=Profile() with { Authentication="VaultPassword",UserName="worker",PasswordReference=new("password") };
        var vault=new RotatingVault();await using var first=await SqlConnectionBuilder.BuildAsync(profile,"Target",vault,default);vault.Value="second-secret-sentinel";
        await using var second=await SqlConnectionBuilder.BuildAsync(profile,"Target",vault,default);
        Assert.Equal("first-secret-sentinel",new SqlConnectionStringBuilder(first.ConnectionString).Password);
        Assert.Equal("second-secret-sentinel",new SqlConnectionStringBuilder(second.ConnectionString).Password);
        Assert.Equal(System.Data.ConnectionState.Closed,first.State);Assert.Equal(System.Data.ConnectionState.Closed,second.State);
    }
    private sealed class RotatingVault : ISecretVault
    {
        public string Value {get;set;}="first-secret-sentinel";
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference,CancellationToken ct)=>ValueTask.FromResult(new SecretValue(Value));
    }
    private sealed class SentinelVault : ISecretVault
    {public ValueTask<SecretValue> ResolveAsync(SecretReference reference,CancellationToken ct)=>throw new InvalidOperationException("secret-sentinel");}}
