using System.Runtime.CompilerServices;
using WorkerDock.Application.Common;

[assembly: InternalsVisibleTo("WorkerDock.UnitTests")]

namespace WorkerDock.ControlCenter.Registry;

public sealed record TenantRegistrationSnapshot(
    Guid Id,
    string DisplayName,
    Guid ExpectedInstanceId,
    string DashboardUrl,
    string HealthCredentialReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CentralJournalMutation(
    string Action,
    Guid RegistrationId,
    TenantRegistrationSnapshot? Before,
    TenantRegistrationSnapshot? After,
    DateTimeOffset AtUtc,
    ActorContext Actor,
    string SafeDetailsJson);

public sealed record CentralJournalRecord(long Sequence, CentralJournalMutation Mutation);

internal interface ICentralJournalWriter
{
    ValueTask AppendAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);
}
