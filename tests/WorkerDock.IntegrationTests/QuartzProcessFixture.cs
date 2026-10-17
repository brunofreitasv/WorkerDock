using System.Diagnostics;
using System.Data;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using WorkerDock.DatabaseMigrator;

namespace WorkerDock.IntegrationTests;

public sealed class QuartzProcessFixture : IAsyncDisposable
{
    public SqlDatabaseFixture Database { get; } = new();
    public MigrationRunner Runner => new(Database.ConnectionString, Database.InstanceId, Database.ScriptsRoot);
    private readonly SqlConnectionStringBuilder runtime;
    private bool probeReady;
    public string RuntimeConnectionString => runtime.ConnectionString;
    private readonly List<Process> processes = [];
    private readonly List<Task> pumps = [];
    private readonly Channel<ProbeEvent> starts = Channel.CreateUnbounded<ProbeEvent>();
    private readonly Channel<ProbeEvent> completions = Channel.CreateUnbounded<ProbeEvent>();
    private readonly Dictionary<Process, Channel<string>> responses = [];
    private readonly object sync = new();
    private int active;
    private int maximumActive;
    public int Active { get { lock (sync) return active; } }
    public int MaximumActive { get { lock (sync) return maximumActive; } }
    public Dictionary<string, int> Deliveries { get; } = [];

    public QuartzProcessFixture() => runtime = QuartzRuntimeConnection.ForDatabase(
        Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_RUNTIME"), Database.ConnectionString);

    public async Task CreateAsync(bool snapshot)
    {
        await Database.CreateAsync(snapshot);
        await Runner.ApplyAsync("tenant", "0002_quartz", default);
        await RunProbeScriptAsync("quartz-probe.up.sql");
        probeReady = true;
        await using var connection = new SqlConnection(RuntimeConnectionString);
        await connection.OpenAsync();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT CASE WHEN IS_MEMBER('WorkerDockRuntime') = 1 AND IS_MEMBER('db_owner') = 0 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 0 THEN 1 ELSE 0 END";
        Assert.Equal(1, Convert.ToInt32(await check.ExecuteScalarAsync()));
    }
    private async Task RunProbeScriptAsync(string file)
    {
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", file));
        command.Parameters.Add(new SqlParameter("@RuntimeLogin", SqlDbType.NVarChar, 128) { Value = runtime.UserID });
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
    public async Task<Process> StartAsync(string role)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "testhost", "WorkerDock.TestHost.dll"));
        info.Environment["WORKERDOCK_QUARTZ_CONNECTION"] = RuntimeConnectionString;
        info.Environment["WORKERDOCK_QUARTZ_INSTANCE"] = Database.InstanceId.ToString();
        info.Environment["WORKERDOCK_QUARTZ_BOOT"] = Guid.NewGuid().ToString("N");
        info.Environment["WORKERDOCK_QUARTZ_ROLE"] = role;
        var process = Process.Start(info) ?? throw new InvalidOperationException("TestHostStartFailed");
        processes.Add(process);
        var replies = Channel.CreateUnbounded<string>();
        responses.Add(process, replies);
        pumps.Add(PumpAsync(process, replies));
        // Drain stderr without exposing driver/configuration details in test output.
        pumps.Add(process.StandardError.ReadToEndAsync());
        Assert.Equal("READY Created", await replies.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        return process;
    }
    private async Task PumpAsync(Process process, Channel<string> replies)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var parts = line.Split(' ');
                if (parts[0] == "START")
                {
                    lock (sync)
                    {
                        active++;
                        maximumActive = Math.Max(maximumActive, active);
                        Deliveries[parts[1]] = Deliveries.GetValueOrDefault(parts[1]) + 1;
                    }
                    await starts.Writer.WriteAsync(new(process, parts[1]));
                }
                else if (parts[0] == "END")
                {
                    lock (sync) active--;
                    await completions.Writer.WriteAsync(new(process, parts[1]));
                }
                else await replies.Writer.WriteAsync(line);
            }
            replies.Writer.TryComplete(new InvalidOperationException("TestHostExited"));
        }
        catch (Exception error) { replies.Writer.TryComplete(error); }
    }
    public async Task PublishAsync(Process publisher, string delivery)
    {
        await publisher.StandardInput.WriteLineAsync("PUBLISH " + delivery);
        await publisher.StandardInput.FlushAsync();
        Assert.Equal("PUBLISHED " + delivery, await responses[publisher].Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
    }
    public Task<ProbeEvent> NextStartedAsync() => starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    public Task<ProbeEvent> NextCompletedAsync() => completions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    public async Task ArmAsync(Process worker)
    {
        await SendAsync(worker, "ARM", "ARMED Running");
    }
    public async Task StandbyAsync(Process worker)
    {
        await SendAsync(worker, "STANDBY", "STANDBY Standby");
    }
    public async Task PingAsync(Process worker) => await SendAsync(worker, "PING", "PONG");
    private async Task SendAsync(Process process, string command, string expected)
    {
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
        Assert.Equal(expected, await responses[process].Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
    }
    public async Task<(int Active, int MaximumActive)> ProbeStateAsync()
    {
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Active, MaximumActive FROM dbo.QuartzProbeState WHERE Id = 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
    public Task<int> DistinctWorkersAsync() => Database.ScalarAsync("SELECT COUNT(DISTINCT BootId) FROM dbo.QuartzProbeEvents WHERE EventKind = 'S'");
    public Task<int> StartedRowsAsync() => Database.ScalarAsync("SELECT COUNT(*) FROM dbo.QuartzProbeEvents WHERE EventKind = 'S'");
    public Task<int> EndedRowsAsync() => Database.ScalarAsync("SELECT COUNT(*) FROM dbo.QuartzProbeEvents WHERE EventKind = 'E'");
    public async Task<IReadOnlyDictionary<string, long>> ClusterCheckinsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT INSTANCE_NAME, LAST_CHECKIN_TIME FROM dbo.WDE_QRTZ_SCHEDULER_STATE WHERE SCHED_NAME = N'WorkerDock.Execute'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var checkins = new Dictionary<string, long>();
        while (await reader.ReadAsync(ct)) checkins.Add(reader.GetString(0), reader.GetInt64(1));
        return checkins;
    }
    public async Task WaitForClusterCycleAsync(IReadOnlyDictionary<string, long> baseline)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var current = await ClusterCheckinsAsync(timeout.Token);
            if (baseline.All(node => current.TryGetValue(node.Key, out var time) && time > node.Value)) return;
            await Task.Delay(100, timeout.Token);
        }
    }
    public async Task ReleaseAsync(ProbeEvent probe)
    {
        await probe.Process.StandardInput.WriteLineAsync("RELEASE " + probe.Delivery);
        await probe.Process.StandardInput.FlushAsync();
    }
    public async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        await process.StandardInput.WriteLineAsync("STOP");
        await process.StandardInput.FlushAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var process in processes)
        {
            if (!process.HasExited)
            {
                try { await StopAsync(process); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            await process.WaitForExitAsync();
        }
        await Task.WhenAll(pumps);
        foreach (var process in processes) process.Dispose();
        try { if (probeReady) await RunProbeScriptAsync("quartz-probe.down.sql"); }
        finally { await Database.DisposeAsync(); }
    }
    public sealed record ProbeEvent(Process Process, string Delivery);
}
