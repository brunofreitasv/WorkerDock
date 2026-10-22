using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Secrets;

namespace WorkerDock.UnitTests;

public sealed class TenantSqlConnectionTests
{
    [Theory]
    [InlineData("Host")]
    [InlineData("Port")]
    [InlineData("Database")]
    [InlineData("User")]
    [InlineData("PasswordReference")]
    public void Read_rejects_missing_leaves(string leaf)
    {
        var values = ValidConfiguration();
        values.Remove("Tenant:Sql:" + leaf);
        var error = Assert.Throws<InvalidOperationException>(() => TenantSqlConfiguration.Read(Config(values), "Tenant:Sql"));
        Assert.Equal("SqlConfigurationInvalid", error.Message);
    }

    [Theory]
    [InlineData("Port", "0")]
    [InlineData("Port", "65536")]
    [InlineData("Port", "not-a-port")]
    [InlineData("Host", "db,1444")]
    [InlineData("Host", "db\\INSTANCE")]
    [InlineData("Host", "db;Password=x")]
    [InlineData("Host", "tcp:db")]
    [InlineData("Host", "db..")]
    [InlineData("Host", "db.example:1433")]
    [InlineData("Host", "db\nexample")]
    [InlineData("Host", " db.example")]
    [InlineData("Database", "")]
    [InlineData("Database", "db\nname")]
    [InlineData("User", "")]
    [InlineData("User", "user\tname")]
    [InlineData("PasswordReference", "")]
    [InlineData("PasswordReference", "secret/path")]
    [InlineData("PasswordReference", "secrét")]
    public async Task Invalid_configuration_fails_before_vault_lookup(string leaf, string value)
    {
        var values = ValidConfiguration();
        values["Tenant:Sql:" + leaf] = value;
        var vault = new RecordingVault();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TenantSqlConnectionResolver.ResolveAsync(TenantSqlConfiguration.Read(Config(values), "Tenant:Sql"), vault, default));
        Assert.Equal("SqlConfigurationInvalid", error.Message);
        Assert.Equal(0, vault.Calls);
    }

    [Theory]
    [InlineData("Database")]
    [InlineData("User")]
    [InlineData("PasswordReference")]
    public void Read_rejects_oversized_fields(string leaf)
    {
        var values = ValidConfiguration();
        values["Tenant:Sql:" + leaf] = new string('a', 129);
        Assert.Equal("SqlConfigurationInvalid", Assert.Throws<InvalidOperationException>(() =>
            TenantSqlConfiguration.Read(Config(values), "Tenant:Sql")).Message);
    }

    [Theory]
    [InlineData("db.example", "1")]
    [InlineData("127.0.0.1", "65535")]
    [InlineData("2001:db8::1", "1433")]
    public void Read_accepts_dns_and_ip_hosts_and_port_boundaries(string host, string port)
    {
        var values = ValidConfiguration();
        values["Tenant:Sql:Host"] = host;
        values["Tenant:Sql:Port"] = port;
        var settings = TenantSqlConfiguration.Read(Config(values), "Tenant:Sql");
        Assert.Equal(host, settings.Host);
        Assert.Equal(int.Parse(port), settings.Port);
        Assert.Equal(new SecretReference("tenant.database.password"), settings.PasswordReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Server=legacy;Password=SENTINEL")]
    public void RejectLegacy_rejects_present_values_including_empty(string value)
    {
        var values = ValidConfiguration();
        values["Tenant:ConnectionString"] = value;
        var error = Assert.Throws<InvalidOperationException>(() => TenantSqlConfiguration.RejectLegacy(Config(values), "Tenant:ConnectionString"));
        Assert.Equal("LegacyConnectionStringUnsupported", error.Message);
        Assert.DoesNotContain("SENTINEL", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void RejectLegacy_accepts_absent_key() =>
        TenantSqlConfiguration.RejectLegacy(Config(ValidConfiguration()), "Tenant:ConnectionString");

    [Fact]
    public async Task Resolve_builds_encrypted_sql_connection_and_disposes_secret()
    {
        var vault = new RecordingVault();
        var settings = TenantSqlConfiguration.Read(Config(ValidConfiguration()), "Tenant:Sql");
        var result = new SqlConnectionStringBuilder(await TenantSqlConnectionResolver.ResolveAsync(settings, vault, default));
        Assert.Equal("tcp:db.example,1433", result.DataSource);
        Assert.Equal("tenant;database", result.InitialCatalog);
        Assert.Equal("tenant;user", result.UserID);
        Assert.Equal("p;ass\"word", result.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, result.Encrypt);
        Assert.False(result.TrustServerCertificate);
        Assert.False(result.PersistSecurityInfo);
        Assert.False(result.IntegratedSecurity);
        Assert.Equal(new SecretReference("tenant.database.password"), vault.Reference);
        Assert.Equal(1, vault.Calls);
        Assert.Throws<ObjectDisposedException>(() => vault.Value.Reveal());
    }

    [Fact]
    public async Task Resolve_validates_settings_created_without_Read()
    {
        var vault = new RecordingVault();
        var settings = new TenantSqlSettings("db;Password=SENTINEL", 1433, "database", "user", new("tenant.database.password"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TenantSqlConnectionResolver.ResolveAsync(settings, vault, default));
        Assert.Equal("SqlConfigurationInvalid", error.Message);
        Assert.Equal(0, vault.Calls);
    }

    [Fact]
    public async Task Resolve_redacts_provider_failure()
    {
        var settings = TenantSqlConfiguration.Read(Config(ValidConfiguration()), "Tenant:Sql");
        var error = await Assert.ThrowsAsync<SecretUnavailableException>(async () =>
            await TenantSqlConnectionResolver.ResolveAsync(settings, new ThrowingVault(), default));
        Assert.Equal("SecretUnavailable", error.SafeCode);
        Assert.DoesNotContain("SENTINEL", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Resolve_preserves_cancellation_without_provider_details()
    {
        using var cancellation = new CancellationTokenSource();
        var settings = TenantSqlConfiguration.Read(Config(ValidConfiguration()), "Tenant:Sql");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await TenantSqlConnectionResolver.ResolveAsync(settings, new CancellingVault(cancellation), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain("SENTINEL", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Tenant_sql_development_vault_requires_development_environment_and_explicit_flag()
    {
        const string flag = "WORKERDOCK_DEVELOPMENT_VAULT";
        const string map = "WORKERDOCK_DEVELOPMENT_SECRET_MAP";
        const string variable = "WORKERDOCK_TASK1_TEST_PASSWORD";
        var previous = new[] { Environment.GetEnvironmentVariable(flag), Environment.GetEnvironmentVariable(map), Environment.GetEnvironmentVariable(variable) };
        try
        {
            Environment.SetEnvironmentVariable(flag, "1");
            Environment.SetEnvironmentVariable(map, "{\"tenant.database.password\":\"WORKERDOCK_TASK1_TEST_PASSWORD\"}");
            Environment.SetEnvironmentVariable(variable, "synthetic-password");
            await Assert.ThrowsAsync<SecretUnavailableException>(async () =>
                await SecretVaultRegistration.CreateTenantSql(false).ResolveAsync(new("tenant.database.password"), default));
            using var secret = await SecretVaultRegistration.CreateTenantSql(true).ResolveAsync(new("tenant.database.password"), default);
            Assert.Equal("synthetic-password", secret.Reveal().ToString());
            Environment.SetEnvironmentVariable(flag, null);
            await Assert.ThrowsAsync<SecretUnavailableException>(async () =>
                await SecretVaultRegistration.CreateTenantSql(true).ResolveAsync(new("tenant.database.password"), default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(flag, previous[0]);
            Environment.SetEnvironmentVariable(map, previous[1]);
            Environment.SetEnvironmentVariable(variable, previous[2]);
        }
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["Tenant:Sql:Host"] = "db.example",
        ["Tenant:Sql:Port"] = "1433",
        ["Tenant:Sql:Database"] = "tenant;database",
        ["Tenant:Sql:User"] = "tenant;user",
        ["Tenant:Sql:PasswordReference"] = "tenant.database.password"
    };

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingVault : ISecretVault
    {
        public int Calls { get; private set; }
        public SecretReference? Reference { get; private set; }
        public SecretValue Value { get; } = new("p;ass\"word");
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            Calls++;
            Reference = reference;
            return ValueTask.FromResult(Value);
        }
    }

    private sealed class ThrowingVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) =>
            throw new InvalidOperationException("SENTINEL provider details");
    }

    private sealed class CancellingVault(CancellationTokenSource cancellation) : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            cancellation.Cancel();
            throw new OperationCanceledException("SENTINEL provider details", ct);
        }
    }
}
