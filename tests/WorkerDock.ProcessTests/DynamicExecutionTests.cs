using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Runtime.Ipc;
using WorkerDock.Runtime.Processes;

namespace WorkerDock.ProcessTests;

public sealed class DynamicExecutionTests
{
    [Fact]
    public async Task New_runtime_executes_legacy_fixed_job_without_context()
    {
        var path = Directory.CreateTempSubdirectory("WorkerDockLegacy_").FullName;
        try
        {
            var descriptor = FixtureProgram.Descriptor("resolved", path) with
            { TemplateVersion = "1.0.0", Configuration = JsonSerializer.SerializeToElement(new { path, value = "fixed" }) };
            await using var child = await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default);
            var result = await child.WaitAsync(default);
            Assert.Equal("Success", result.Outcome);
            Assert.True(File.Exists(Path.Combine(path, "entries")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Resolution_error_returns_permanent_failure_without_handler_or_secret_lookup()
    {
        var path = Directory.CreateTempSubdirectory("WorkerDockError_").FullName;
        try
        {
            var descriptor = DynamicDescriptor(path) with
            { ValueSnapshot = null, ValueResolutionError = "DynamicSourceUnavailable" };
            await using var child = await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default);
            var result = await child.WaitAsync(default);
            Assert.Equal("PermanentFailure", result.Outcome);
            Assert.False(result.EffectsMayHaveOccurred);
            Assert.Equal("DynamicSourceUnavailable", result.SafeCode);
            Assert.False(File.Exists(Path.Combine(path, "entries")));
            Assert.False(File.Exists(Path.Combine(path, "secret-lookups")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("unknown-target")]
    [InlineData("unsupported-resolver")]
    public async Task Invalid_dynamic_snapshot_never_enters_handler(string fault)
    {
        var path = Directory.CreateTempSubdirectory("WorkerDockInvalid_").FullName;
        try
        {
            var descriptor = DynamicDescriptor(path);
            descriptor = fault switch
            {
                "missing" => descriptor with { ValueSnapshot = null },
                "mismatch" => descriptor with { Configuration = JsonSerializer.SerializeToElement(new { path, value = "forged" }) },
                "unknown-target" => descriptor with { ValueSnapshot = descriptor.ValueSnapshot! with { Values = new Dictionary<string, JsonElement> { ["/path"] = JsonSerializer.SerializeToElement("wrong") } } },
                _ => descriptor with { ValueContext = descriptor.ValueContext! with { ResolverVersion = 99 } }
            };
            await using var child = await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default);
            var result = await child.WaitAsync(default);
            Assert.Equal("PermanentFailure", result.Outcome);
            Assert.False(result.EffectsMayHaveOccurred);
            Assert.False(File.Exists(Path.Combine(path, "entries")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Actual_resolved_descriptor_bound_includes_value_snapshot()
    {
        var path = Directory.CreateTempSubdirectory("WorkerDockBound_").FullName;
        try
        {
            var descriptor = DynamicDescriptor(path) with
            { IntegrationConfiguration = JsonSerializer.SerializeToElement(new { padding = new string('x', 55_350) }) };
            Assert.True(ExecutionPayloadLimits.FitsPreparedDescriptor(descriptor with { ValueSnapshot = null }));
            Assert.False(ExecutionPayloadLimits.FitsPreparedDescriptor(descriptor));
            var error = await Assert.ThrowsAsync<ExecutionProcessException>(async () =>
                await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default));
            Assert.Equal("IpcMessageTooLarge", error.SafeCode);
            Assert.True(error.NoExternalEffects);
            Assert.False(File.Exists(Path.Combine(path, "entries")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Oversized_actual_snapshot_is_rejected_before_child_start()
    {
        var path = Directory.CreateTempSubdirectory("WorkerDockSnapshotBound_").FullName;
        try
        {
            var value = new string('x', 17_000);
            var descriptor = DynamicDescriptor(path) with
            {
                Configuration = JsonSerializer.SerializeToElement(new { path, value }),
                ValueSnapshot = new(1, new string('A', 64), new Dictionary<string, JsonElement> { ["/value"] = JsonSerializer.SerializeToElement(value) })
            };
            var error = await Assert.ThrowsAsync<ExecutionProcessException>(async () =>
                await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default));
            Assert.Equal("IpcMessageTooLarge", error.SafeCode);
            Assert.True(error.NoExternalEffects);
            Assert.False(File.Exists(Path.Combine(path, "entries")));
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Protocol_v1_peer_is_rejected_before_handler_entry()
    {
        Assert.Equal(2, ExecutionProtocol.CurrentVersion);
        var path = Directory.CreateTempSubdirectory("WorkerDockV1_").FullName;
        try
        {
            var supervisor = new ExecutionSupervisor(FixtureProgram.Options("bad-version"));
            await Assert.ThrowsAsync<ExecutionProcessException>(async () =>
                await supervisor.StartAsync(Guid.NewGuid(), 1, FixtureProgram.Descriptor("resolved", path), default));
            Assert.False(File.Exists(Path.Combine(path, "entries")));
        }
        finally { Directory.Delete(path, true); }
    }

    private static ExecutionDescriptor DynamicDescriptor(string path)
    {
        var value = JsonSerializer.SerializeToElement("2026-10-20");
        return FixtureProgram.Descriptor("resolved", path) with
        {
            TemplateVersion = "1.1.0", Configuration = JsonSerializer.SerializeToElement(new { path, value = "2026-10-20" }),
            ValueContext = new(1, "UTC", "Manual", null, DateTimeOffset.Parse("2026-10-20T12:00:00Z"), null, DateTimeOffset.Parse("2026-10-20T12:00:00Z")),
            ValueSnapshot = new(1, new string('A', 64), new Dictionary<string, JsonElement> { ["/value"] = value })
        };
    }
}
