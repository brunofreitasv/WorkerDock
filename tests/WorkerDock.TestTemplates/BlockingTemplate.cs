using System.Text.Json;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.TestTemplates;
public class BlockingTemplate : IWorkerTemplate
{
    public virtual TemplateDescriptor Descriptor => new("block", "1", "Blocking test", "Test only", JsonSerializer.SerializeToElement(new { type = "object" }), JsonSerializer.SerializeToElement(new { }), "test-v1");
    public IReadOnlyList<ValidationIssue> Validate(JsonElement configuration) => [];
    public virtual async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(context.Execution.Configuration.GetProperty("path").GetString()!, "entries"), "entered");
        await Task.Delay(Timeout.InfiniteTimeSpan); return null!;
    }
}
public sealed class SecretTemplate : BlockingTemplate
{
    public override TemplateDescriptor Descriptor => base.Descriptor with { Id = "secret" };
    public override async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        using var secret = await context.Secrets.ResolveAsync(new SecretReference("test"), ct);
        if (secret.Reveal().Length == 0) throw new InvalidOperationException();
        Console.WriteLine(new string(secret.Reveal().Span)); Console.Error.WriteLine(new string(secret.Reveal().Span));
        var path = context.Execution.Configuration.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(path, "arguments"), string.Join(" ", Environment.GetCommandLineArgs()), ct);
        await context.Log.WriteAsync("SecretTest", new Dictionary<string, string> { [new string(secret.Reveal().Span)] = new string(secret.Reveal().Span) }, ct);
        return new("Success", "SecretResolved", false, JsonSerializer.SerializeToElement(new { secret = new string(secret.Reveal().Span) }), null);
    }
}

public sealed class DiagnosticTemplate : BlockingTemplate
{
    public override TemplateDescriptor Descriptor => base.Descriptor with { Id = "diagnostic" };
    public override async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        await context.Log.WriteAsync("TemplateFailed", new Dictionary<string, string> { ["safeCode"] = "ConnectivityFailed" }, ct);
        return new("Success", "DiagnosticCaptured", false, JsonSerializer.SerializeToElement(new { }), null);
    }
}

public sealed class DiagnosticStressTemplate : BlockingTemplate
{
    public override TemplateDescriptor Descriptor => base.Descriptor with { Id = "diagnostic-stress" };
    public override async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        await context.Log.WriteAsync("TemplateFailed", new Dictionary<string, string> { ["message"] = "SHOULD_BE_DROPPED" }, ct);
        for (var i = 0; i < 10_000; i++)
            await context.Log.WriteAsync("TemplateCompleted", new Dictionary<string, string> { ["outcome"] = "Success" }, ct);
        return new("Success", "DiagnosticFloodDropped", false, JsonSerializer.SerializeToElement(new { }), null);
    }
}
