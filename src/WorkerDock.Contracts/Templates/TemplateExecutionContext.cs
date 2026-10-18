using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Contracts.Templates;
public sealed record TemplateExecutionContext(ExecutionDescriptor Execution, ISecretVault Secrets, ISafeExecutionLog Log);
