using System.Diagnostics;
using System.Threading.Channels;
using WorkerDock.DatabaseMigrator;

namespace WorkerDock.IntegrationTests;

public sealed class QuartzProcessFixture : IAsyncDisposable
{
    public SqlDatabaseFixture Database { get; } = new();
    public MigrationRunner Runner => new(Database.ConnectionString, Database.InstanceId, Database.ScriptsRoot);
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

    public async Task CreateAsync(bool snapshot)
    {
        await Database.CreateAsync(snapshot);
        await Runner.ApplyAsync("tenant", "0002_quartz", default);
    }
    public async Task<Process> StartAsync(string role)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "testhost", "WorkerDock.TestHost.dll"));
        info.Environment["WORKERDOCK_QUARTZ_CONNECTION"] = Database.ConnectionString;
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
        await Database.DisposeAsync();
    }
    public sealed record ProbeEvent(Process Process, string Delivery);
}
