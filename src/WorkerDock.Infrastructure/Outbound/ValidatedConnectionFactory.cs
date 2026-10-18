using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using WorkerDock.Application.Outbound;
using System.Net.Http;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Infrastructure.Outbound;

/// <summary>Connects only to freshly approved IPs while TLS continues to use the URI hostname for SNI and validation.</summary>
public sealed class ValidatedConnectionFactory(IOutboundPolicy policy)
{
    private static readonly TimeSpan DestinationPreflightTimeout = TimeSpan.FromSeconds(5);

    public SocketsHttpHandler CreateHandler(string policyName)
    {
        if (policyName is not ("tenant-jobs" or "tenant-alerts" or "central-health")) throw new OutboundPolicyException("OutboundPolicyUnavailable");
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxResponseHeadersLength = 32,
            SslOptions = { EnabledSslProtocols = SslProtocols.None },
            ConnectCallback = async (context, ct) => await ConnectAsync(context, policyName, ct)
        };
    }

    public HttpClient CreateClient(string policyName)
    {
        var client = new HttpClient(new PreflightHandler(policy, policyName) { InnerHandler = CreateHandler(policyName) }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
        return client;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, string policyName, CancellationToken ct)
    {
        var requestUri = context.InitialRequestMessage?.RequestUri;
        if (requestUri is null || !requestUri.IsAbsoluteUri || requestUri.Scheme is not ("http" or "https") || requestUri.Port != context.DnsEndPoint.Port)
            throw new OutboundDestinationDeniedException("OutboundTargetInvalid");
        var uri = new UriBuilder(requestUri.Scheme, context.DnsEndPoint.Host, context.DnsEndPoint.Port).Uri;
        ApprovedDestination approved;
        try { approved = await policy.ResolveAsync(new(uri, policyName), ct); }
        catch (OutboundPolicyException error) { throw new OutboundDestinationDeniedException(error.SafeCode); }
        if (approved.Port != context.DnsEndPoint.Port || !string.Equals(approved.Host, OutboundPolicy.NormalizeHost(context.DnsEndPoint.Host), StringComparison.Ordinal))
            throw new OutboundDestinationDeniedException("OutboundDestinationChanged");
        Exception? last = null;
        foreach (var address in approved.Addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, approved.Port), ct);
                if (socket.RemoteEndPoint is not IPEndPoint remote || remote.Port != approved.Port || !approved.Addresses.Contains(OutboundPolicy.NormalizeAddress(remote.Address)))
                    throw new OutboundDestinationDeniedException("OutboundPeerMismatch");
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException or OutboundDestinationDeniedException)
            {
                last = error; socket.Dispose();
                if (error is OperationCanceledException) throw;
            }
        }
        throw new OutboundDestinationDeniedException(last is SocketException ? "OutboundConnectFailed" : "OutboundPeerMismatch");
    }

    private sealed class PreflightHandler(IOutboundPolicy policy, string policyName) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null) throw new OutboundPolicyException("OutboundTargetInvalid");
            using var preflight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preflight.CancelAfter(DestinationPreflightTimeout);
            try { await policy.ResolveAsync(new(request.RequestUri, policyName), preflight.Token).AsTask().WaitAsync(preflight.Token); }
            catch (OutboundPolicyException error) { throw new OutboundDestinationDeniedException(error.SafeCode); }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
