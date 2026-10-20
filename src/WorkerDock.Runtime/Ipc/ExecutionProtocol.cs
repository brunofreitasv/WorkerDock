using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Runtime.Processes;
namespace WorkerDock.Runtime.Ipc;

public sealed record Handshake(int Version, Guid AttemptId, long Generation, string Nonce, int ProcessId);
public sealed record ExecuteRequest(int Version, Guid AttemptId, long Generation, string MetadataDigest, ExecutionDescriptor Execution);
public sealed record ExecutionControl(string Kind);
public sealed record ExecutorDiagnosticFrame(string Kind, long Sequence, string EventCode, IReadOnlyDictionary<string, string> SafeFields);

/// <summary>Version 2: bounded little-endian length-prefixed UTF-8 JSON, one attempt per pipe.</summary>
public static class ExecutionProtocol
{
    public const int CurrentVersion = 2;
    public const int MaximumMessageBytes = ExecutionPayloadLimits.MaximumMessageBytes;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    public static byte[] Serialize<T>(T message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumMessageBytes) throw new ExecutionProcessException("IpcMessageTooLarge");
        return bytes;
    }
    public static async ValueTask WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var payload = Serialize(message);
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(payload, ct); await stream.FlushAsync(ct);
    }
    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var payload = await ReadPayloadAsync(stream, ct);
        try { return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new ExecutionProcessException("IpcInvalidMessage"); }
        catch (JsonException) { throw new ExecutionProcessException("IpcInvalidMessage"); }
    }
    public static async ValueTask<byte[]> ReadPayloadAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > MaximumMessageBytes) throw new ExecutionProcessException("IpcMessageTooLarge");
        var payload = new byte[size]; await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
    public static bool ClientIs(NamedPipeServerStream pipe, int processId) => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) && id == processId;
    public static bool ServerIs(NamedPipeClientStream pipe, int processId) => GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id) && id == processId;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);
}
