using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using WorkerDock.Application.Execution;
using WorkerDock.Application.Observability;
using WorkerDock.Contracts.Execution;
using WorkerDock.Runtime.Ipc;
namespace WorkerDock.Runtime.Processes;

/// <summary>Deployment-authored paths, bootstrap arguments and exact catalog digests; never job input.</summary>
public sealed record ExecutionSupervisorOptions(string ExecutablePath, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> MetadataDigests)
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
public sealed class ExecutionProcessException(string safeCode, bool noExternalEffects = false) : Exception(safeCode)
{
    public string SafeCode { get; } = safeCode;
    public bool NoExternalEffects { get; } = noExternalEffects;
}
public sealed class ExecutionSupervisor : IExecutionSupervisor
{
    private readonly ExecutionSupervisorOptions options;
    private readonly Func<Process, ContainedProcess> contain;
    private readonly IExecutionEvents events;
    public ExecutionSupervisor(ExecutionSupervisorOptions options) : this(options, NullExecutionEvents.Instance, process => new ContainedProcess(process)) { }
    public ExecutionSupervisor(ExecutionSupervisorOptions options, IExecutionEvents events) : this(options, events, process => new ContainedProcess(process)) { }
    internal ExecutionSupervisor(ExecutionSupervisorOptions options, Func<Process, ContainedProcess> contain)
        : this(options, NullExecutionEvents.Instance, contain) { }
    internal ExecutionSupervisor(ExecutionSupervisorOptions options, IExecutionEvents events, Func<Process, ContainedProcess> contain)
    {
        if (options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options with { Arguments = options.Arguments.ToArray(), MetadataDigests = new Dictionary<string, string>(options.MetadataDigests, StringComparer.Ordinal) };
        this.contain = contain;
        this.events = events;
    }
    public async ValueTask<IExecutionChild> StartAsync(Guid attemptId, long generation, ExecutionDescriptor descriptor, CancellationToken ct)
    {
        if (attemptId == Guid.Empty || generation <= 0 || descriptor.ExecutionId == Guid.Empty || descriptor.AttemptOrdinal <= 0)
            throw new ExecutionProcessException("InvalidExecutionIdentity", true);
        var digest = options.MetadataDigests.GetValueOrDefault(descriptor.TemplateId + "@" + descriptor.TemplateVersion) ?? "";
        var request = new ExecuteRequest(1, attemptId, generation, digest, descriptor);
        try { _ = ExecutionProtocol.Serialize(request); } // No child has been started.
        catch (ExecutionProcessException error) { throw new ExecutionProcessException(error.SafeCode, true); }
        catch (System.Text.Json.JsonException) { throw new ExecutionProcessException("IpcInvalidMessage", true); }
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var name = "WorkerDock_" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        Process? process = null; ContainedProcess? containment = null;
        var permissionAttempted = false;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(options.StartupTimeout);
        if (descriptor.DeadlineUtc is { } deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { await pipe.DisposeAsync(); throw new ExecutionProcessException("ExecutorDeadlineExpired", true); }
            if (remaining < options.StartupTimeout) startup.CancelAfter(remaining);
        }
        try
        {
            var info = new ProcessStartInfo(options.ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in options.Arguments) info.ArgumentList.Add(argument);
            info.Environment["WORKERDOCK_PIPE"] = name;
            info.Environment["WORKERDOCK_NONCE"] = nonce;
            info.Environment["WORKERDOCK_ATTEMPT"] = attemptId.ToString("D");
            info.Environment["WORKERDOCK_GENERATION"] = generation.ToString(CultureInfo.InvariantCulture);
            info.Environment["WORKERDOCK_PARENT"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            process = Process.Start(info) ?? throw new ExecutionProcessException("ExecutorStartupFailed");
            // Bootstrap code waits for permission. No template factory is invoked before this succeeds.
            containment = contain(process);
            var stdout = DiscardAsync(process.StandardOutput.BaseStream);
            var stderr = DiscardAsync(process.StandardError.BaseStream);
            await pipe.WaitForConnectionAsync(startup.Token);
            if (!ExecutionProtocol.ClientIs(pipe, process.Id)) throw new ExecutionProcessException("ExecutorIdentityRejected");
            var hello = await ExecutionProtocol.ReadAsync<Handshake>(pipe, startup.Token);
            if (hello.Version != 1 || hello.AttemptId != attemptId || hello.Generation != generation || hello.ProcessId != process.Id ||
                hello.Nonce is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Nonce), Encoding.UTF8.GetBytes(nonce)))
                throw new ExecutionProcessException("ExecutorIdentityRejected");
            // This frame is the sole permission to enter the installed handler composition root.
            permissionAttempted = true;
            await ExecutionProtocol.WriteAsync(pipe, request, startup.Token);
            return new ChildHandle(process, containment, pipe, stdout, stderr, events, descriptor.ExecutionId, attemptId);
        }
        catch
        {
            await pipe.DisposeAsync();
            if (containment is not null) await containment.DisposeAsync();
            else if (process is not null)
            {
                if (!process.HasExited) process.Kill(true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
            process?.Dispose();
            throw new ExecutionProcessException("ExecutorStartupFailed", !permissionAttempted);
        }
    }
    private sealed class NullExecutionEvents : IExecutionEvents
    {
        public static readonly NullExecutionEvents Instance = new();
        public ValueTask AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct) => ValueTask.CompletedTask;
    }
    private static async Task DiscardAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
