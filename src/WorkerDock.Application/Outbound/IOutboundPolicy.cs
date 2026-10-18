using System.Net;
namespace WorkerDock.Application.Outbound;
public sealed record OutboundTarget(Uri Uri, string PolicyName);
public sealed record ApprovedDestination(string Host, int Port, IReadOnlyList<IPAddress> Addresses);
public sealed class OutboundPolicyException(string safeCode) : Exception(safeCode) { public string SafeCode { get; } = safeCode; }
public interface IOutboundPolicy { ValueTask<ApprovedDestination> ResolveAsync(OutboundTarget target, CancellationToken ct); }
