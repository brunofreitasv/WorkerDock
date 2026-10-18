namespace WorkerDock.Application.Common;

public sealed record CommandReceipt(Guid CommandId, Guid TargetId, string State);
