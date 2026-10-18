using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Runtime.Ipc;
using WorkerDock.Runtime.Processes;

namespace WorkerDock.ProcessTests;

public sealed class ContainmentTests
{
    [Fact]
    public async Task Supervisor_exit_kills_child_and_grandchild()
    {
        using var fixture = new TestDirectory();
        using var supervisor = Process.Start(FixtureProgram.StartInfo("supervisor", fixture.Path))!;
        try
        {
            var pids = await WaitForPids(fixture.Path);
            Assert.True(Alive(pids[0])); Assert.True(Alive(pids[1]));
            supervisor.Kill(); await supervisor.WaitForExitAsync();
            await AssertGone(pids[0]); await AssertGone(pids[1]);
        }
        finally
        {
            if (!supervisor.HasExited) supervisor.Kill();
            await supervisor.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Force_stop_of_one_noncooperative_child_preserves_sibling()
    {
        using var first = new TestDirectory(); using var second = new TestDirectory();
        var supervisor = FixtureProgram.Supervisor();
        await using var child = await supervisor.StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("spawn", first.Path), default);
        await using var sibling = await supervisor.StartAsync(Guid.NewGuid(), 2, FixtureProgram.Descriptor("spawn", second.Path), default);
        var pids = await WaitForPids(first.Path); var siblingPids = await WaitForPids(second.Path);
        await child.CancelAsync(default);
        Assert.True(Alive(pids[0]));
        await child.TerminateAsync(default);
        await AssertGone(pids[0]); await AssertGone(pids[1]);
        Assert.True(Alive(siblingPids[0])); Assert.True(Alive(siblingPids[1]));
        await sibling.TerminateAsync(default);
        await AssertGone(siblingPids[0]); await AssertGone(siblingPids[1]);
    }

    [Fact]
    public async Task Containment_failure_never_enters_handler()
    {
        using var fixture = new TestDirectory();
        var supervisor = new ExecutionSupervisor(FixtureProgram.Options(), _ => throw new IOException("synthetic containment failure"));
        await Assert.ThrowsAsync<ExecutionProcessException>(async () => await supervisor.StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", fixture.Path), default));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Theory]
    [InlineData("nonce")][InlineData("attempt")][InlineData("generation")][InlineData("pid")][InlineData("peer")]
    public async Task Wrong_handshake_identity_never_enters_handler(string mismatch)
    {
        using var fixture = new TestDirectory();
        var options = FixtureProgram.Options("bad-" + mismatch);
        await Assert.ThrowsAsync<ExecutionProcessException>(async () => await new ExecutionSupervisor(options).StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", fixture.Path), default));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Unsupported_exact_version_never_enters_handler()
    {
        using var fixture = new TestDirectory();
        var descriptor = FixtureProgram.Descriptor("block", fixture.Path) with { TemplateVersion = "2" };
        await using var child = await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default);
        var result = await child.WaitAsync(default);
        Assert.Equal("TemplateVersionUnavailable", result.SafeCode);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Metadata_mismatch_never_enters_handler()
    {
        using var fixture = new TestDirectory();
        var options = FixtureProgram.Options() with { MetadataDigests = new Dictionary<string, string> { ["block@1"] = "different" } };
        await using var child = await new ExecutionSupervisor(options).StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", fixture.Path), default);
        Assert.Equal("TemplateMetadataMismatch", (await child.WaitAsync(default)).SafeCode);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Oversized_frames_rejected_before_payload_allocation()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(1024 * 1024));
        await Assert.ThrowsAsync<ExecutionProcessException>(async () => await ExecutionProtocol.ReadAsync<Handshake>(stream, default));
    }

    [Fact]
    public async Task Startup_is_bounded_and_raw_errors_are_discarded()
    {
        var options = FixtureProgram.Options("silent") with { StartupTimeout = TimeSpan.FromMilliseconds(500) };
        var watch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ExecutionProcessException>(async () => await new ExecutionSupervisor(options).StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", "unused"), default));
        Assert.Equal("ExecutorStartupFailed", failure.SafeCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task Secrets_resolve_only_in_child_and_never_escape_safe_channels()
    {
        using var fixture = new TestDirectory();
        var sentinel = "SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("WORKERDOCK_TEST_SECRET", sentinel);
        try
        {
            var descriptor = FixtureProgram.Descriptor("secret", fixture.Path);
            await using var child = await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default);
            var result = await child.WaitAsync(default);
            Assert.Equal("SecretResolved", result.SafeCode);
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(descriptor));
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(result));
            Assert.DoesNotContain(sentinel, await File.ReadAllTextAsync(System.IO.Path.Combine(fixture.Path, "arguments")));
            Assert.DoesNotContain(sentinel, string.Join("", Directory.GetFiles(fixture.Path).Select(File.ReadAllText)));
            var capture = FixtureProgram.StartInfo("secret-supervisor", fixture.Path); capture.RedirectStandardOutput = true; capture.RedirectStandardError = true;
            using var process = Process.Start(capture)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode); Assert.DoesNotContain(sentinel, await output); Assert.DoesNotContain(sentinel, await error);
        }
        finally { Environment.SetEnvironmentVariable("WORKERDOCK_TEST_SECRET", null); }
    }

    [Fact]
    public async Task Invalid_result_terminates_the_entire_tree_before_wait_returns()
    {
        using var fixture = new TestDirectory();
        await using var child = await new ExecutionSupervisor(FixtureProgram.Options("invalid-result")).StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", fixture.Path), default);
        var pids = await WaitForPids(fixture.Path);
        Assert.Equal("ExecutorDisconnected", (await child.WaitAsync(default)).SafeCode);
        await AssertGone(pids[0]); await AssertGone(pids[1]);
    }
    [Fact]
    public async Task Expired_deadline_never_enters_handler()
    {
        using var fixture = new TestDirectory();
        var request = FixtureProgram.Descriptor("block", fixture.Path) with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var error = await Assert.ThrowsAsync<ExecutionProcessException>(async () => await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, request, default));
        Assert.Equal("ExecutorDeadlineExpired", error.SafeCode);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Oversized_descriptor_never_starts_handler()
    {
        using var fixture = new TestDirectory();
        var request = FixtureProgram.Descriptor("block", fixture.Path) with { Configuration = JsonSerializer.SerializeToElement(new { path = fixture.Path, oversized = new string('a', 100000) }) };
        var error = await Assert.ThrowsAsync<ExecutionProcessException>(async () => await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, request, default));
        Assert.Equal("IpcMessageTooLarge", error.SafeCode);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Native_assignment_failure_never_enters_handler()
    {
        using var fixture = new TestDirectory();
        var supervisor = new ExecutionSupervisor(FixtureProgram.Options(), process =>
        {
            process.Kill(); process.WaitForExit(); return new ContainedProcess(process);
        });
        await Assert.ThrowsAsync<ExecutionProcessException>(async () => await supervisor.StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("block", fixture.Path), default));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }

    [Fact]
    public async Task Child_rejects_wrong_server_process_before_template_composition()
    {
        using var fixture = new TestDirectory();
        var name = "WorkerDockTest_" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = FixtureProgram.StartInfo("executor");
        info.Environment["WORKERDOCK_PIPE"] = name; info.Environment["WORKERDOCK_NONCE"] = "synthetic";
        info.Environment["WORKERDOCK_ATTEMPT"] = Guid.NewGuid().ToString(); info.Environment["WORKERDOCK_GENERATION"] = "1";
        info.Environment["WORKERDOCK_PARENT"] = "0";
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "entries")));
    }
    [Fact]
    public void Secret_value_redacts_and_clears_on_disposal()
    {
        var value = new SecretValue("synthetic".AsSpan());
        Assert.Equal("[REDACTED]", value.ToString());
        Assert.Equal("synthetic", new string(value.Reveal().Span));
        value.Dispose();
        Assert.Throws<ObjectDisposedException>(() => value.Reveal());
    }

    internal static async Task<int[]> WaitForPids(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var path = System.IO.Path.Combine(directory, "pids");
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, timeout.Token);
                if (text.Split(',') is { Length: 2 } parts && int.TryParse(parts[0], out var child) && int.TryParse(parts[1], out var grandchild)) return [child, grandchild];
            }
            await Task.Delay(25, timeout.Token);
        }
    }
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task AssertGone(int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Alive(pid) && !timeout.IsCancellationRequested) await Task.Delay(25);
        Assert.False(Alive(pid), $"Contained process {pid} survived");
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkerDockProcess_" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { Directory.Delete(Path, true); }
    }
}
