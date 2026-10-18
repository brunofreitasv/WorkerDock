using System.Diagnostics;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.TestTemplates;
public sealed class SpawningTemplate(Func<Process> spawn) : BlockingTemplate
{
    public override TemplateDescriptor Descriptor => base.Descriptor with { Id = "spawn" };
    public override async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        using var grandchild = spawn();
        var directory = context.Execution.Configuration.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(directory, "pids.tmp"), $"{Environment.ProcessId},{grandchild.Id}");
        File.Move(Path.Combine(directory, "pids.tmp"), Path.Combine(directory, "pids"));
        await Task.Delay(Timeout.InfiniteTimeSpan); return null!;
    }
}
