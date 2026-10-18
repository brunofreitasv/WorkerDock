using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Executor;
using WorkerDock.Runtime.Ipc;
using WorkerDock.Runtime.Processes;
using WorkerDock.TestTemplates;

namespace WorkerDock.ProcessTests;
public static class FixtureProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args[0] == "grandchild") { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
        if (args[0] == "supervisor")
        {
            await using var child = await Supervisor().StartAsync(Guid.NewGuid(), 1, Descriptor("spawn", args[1]), default);
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        if (args[0] == "secret-supervisor")
        {
            await using var child = await Supervisor().StartAsync(Guid.NewGuid(), 1, Descriptor("secret", args[1]), default);
            Console.WriteLine(JsonSerializer.Serialize(await child.WaitAsync(default))); return 0;
        }
        if (args[0] == "silent") { Console.WriteLine("RAW_CHILD_ERROR"); await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
        if (args[0] == "bad-peer")
        {
            using var peer = Process.Start(StartInfo("bad-nonce"))!;
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        if (args[0] == "invalid-result")
        {
            await using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("WORKERDOCK_PIPE")!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            await ExecutionProtocol.WriteAsync(pipe, new Handshake(1, Guid.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_ATTEMPT")!), long.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_GENERATION")!, System.Globalization.CultureInfo.InvariantCulture), Environment.GetEnvironmentVariable("WORKERDOCK_NONCE")!, Environment.ProcessId), default);
            var request = await ExecutionProtocol.ReadAsync<ExecuteRequest>(pipe, default);
            using var grandchild = Process.Start(StartInfo("grandchild"))!;
            var directory = request.Execution.Configuration.GetProperty("path").GetString()!;
            await File.WriteAllTextAsync(Path.Combine(directory, "pids.tmp"), $"{Environment.ProcessId},{grandchild.Id}");
            File.Move(Path.Combine(directory, "pids.tmp"), Path.Combine(directory, "pids"));
            await pipe.WriteAsync(BitConverter.GetBytes(1024 * 1024)); await pipe.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        if (args[0].StartsWith("bad-", StringComparison.Ordinal))
        {
            await using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("WORKERDOCK_PIPE")!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            var attempt = Guid.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_ATTEMPT")!);
            var generation = long.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_GENERATION")!, System.Globalization.CultureInfo.InvariantCulture);
            var nonce = Environment.GetEnvironmentVariable("WORKERDOCK_NONCE")!;
            var pid = Environment.ProcessId;
            if (args[0] == "bad-nonce") nonce = "wrong";
            if (args[0] == "bad-attempt") attempt = Guid.NewGuid();
            if (args[0] == "bad-generation") generation++;
            if (args[0] == "bad-pid") pid++;
            await ExecutionProtocol.WriteAsync(pipe, new Handshake(1, attempt, generation, nonce, pid), default);
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        return await ExecutorHost.RunAsync(() => [new BlockingTemplate(), new SpawningTemplate(() => Process.Start(StartInfo("grandchild"))!), new SecretTemplate()], execution => new(new TestVault(), new TestLog(execution.Configuration.GetProperty("path").GetString()!)));
    }
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(FixtureProgram).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static ExecutionSupervisor Supervisor() => new(Options());
    public static ExecutionSupervisorOptions Options(string mode = "executor") => new("dotnet", [typeof(FixtureProgram).Assembly.Location, mode], new Dictionary<string, string> { ["block@1"] = "test-v1", ["spawn@1"] = "test-v1", ["secret@1"] = "test-v1" });
    public static ExecutionDescriptor Descriptor(string template, string path) => new(Guid.NewGuid(), 1, template, "1", "stable-idempotency", JsonSerializer.SerializeToElement(new { path }), null, null, JsonSerializer.SerializeToElement(new { version = 1 }), null);
    private sealed class TestVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) => ValueTask.FromResult(new SecretValue(Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SECRET")!.AsSpan()));
    }
    private sealed class TestLog(string path) : ISafeExecutionLog
    {
        public async ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct) => await File.WriteAllTextAsync(Path.Combine(path, "diagnostics"), JsonSerializer.Serialize(new { eventCode, safeFields }), ct);
    }
}
