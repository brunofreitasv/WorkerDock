using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WorkerDock.ProcessTests;

public sealed class TenantSqlHostStartupTests
{
    [Theory]
    [InlineData("Scheduler")]
    [InlineData("Worker")]
    public async Task Missing_password_reference_stops_host_before_sql_without_disclosing_secrets(string host)
    {
        using var sql = new TcpListener(IPAddress.Loopback, 0);
        sql.Start();
        var port = ((IPEndPoint)sql.LocalEndpoint).Port;
        var secretVariable = "WORKERDOCK_SQL_SENTINEL_" + Guid.NewGuid().ToString("N");
        var password = "synthetic-password-" + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(Path.Combine(root, "src", "WorkerDock." + host, "bin", "Release", host == "Scheduler" ? "net10.0" : "net10.0-windows", "WorkerDock." + host + ".dll"));
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("Tenant__", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("Worker__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Executor__", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["DOTNET_ENVIRONMENT"] = "Development";
        info.Environment["Tenant__InstanceId"] = "216ce600-b434-4c9d-910b-cf51d47d1ff5";
        info.Environment["Tenant__Adapter"] = "Sql";
        info.Environment["Tenant__Sql__Host"] = "127.0.0.1";
        info.Environment["Tenant__Sql__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["Tenant__Sql__Database"] = "StartupTest";
        info.Environment["Tenant__Sql__User"] = "startup_user";
        info.Environment["Tenant__Sql__PasswordReference"] = "unmapped.password";
        info.Environment["Worker__InstallationId"] = "f878f9ab-dc2c-42bf-8827-87a493d2c6c7";
        info.Environment["Worker__CatalogDigest"] = new string('a', 64);
        info.Environment["Executor__Path"] = "unused-executor.exe";
        info.Environment["WORKERDOCK_DEVELOPMENT_VAULT"] = "1";
        info.Environment["WORKERDOCK_DEVELOPMENT_SECRET_MAP"] = JsonSerializer.Serialize(new Dictionary<string, string> { ["other.password"] = secretVariable });
        info.Environment[secretVariable] = password;

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;
            Assert.Contains("SecretUnavailable", output);
            Assert.DoesNotContain(password, output);
            Assert.DoesNotContain(secretVariable, output);
            Assert.DoesNotContain("WORKERDOCK_DEVELOPMENT_SECRET_MAP", output);
            Assert.False(sql.Pending(), "Host opened SQL before resolving its password reference.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
