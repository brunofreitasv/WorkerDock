namespace WorkerDock.Application.Common;
public sealed record ActorContext(string Subject, string AuthenticationMode, Guid CorrelationId);
