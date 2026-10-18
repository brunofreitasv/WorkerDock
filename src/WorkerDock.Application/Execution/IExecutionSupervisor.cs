using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Application.Execution;
public interface IExecutionSupervisor
{
    ValueTask<IExecutionChild> StartAsync(Guid attemptId, long generation, ExecutionDescriptor descriptor, CancellationToken ct);
}
public interface IExecutionChild : IAsyncDisposable
{
    ValueTask<TemplateResult> WaitAsync(CancellationToken ct);
    ValueTask CancelAsync(CancellationToken ct);
    ValueTask TerminateAsync(CancellationToken ct);
}
