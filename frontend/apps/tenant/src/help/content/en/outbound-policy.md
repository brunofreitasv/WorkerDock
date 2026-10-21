# Control outbound destinations

## Prerequisites

You need **Manage** access. Ask the network or security owner for the approved host names, ports, and private CIDRs required by HTTP jobs or alert webhooks. The tenant has separate `tenant-jobs` and `tenant-alerts` policies.

## Role

Manage users can update outbound policy. Destinations are checked when a request executes, so saving a policy does not prove that a remote service is reachable.

## Steps

1. Open [Settings](#/settings) and select **Outbound policy**. Choose **tenant-jobs** for HTTP jobs or **tenant-alerts** for alert webhooks.
2. Enter approved hosts, one per line. Wildcards such as `*.example.com` broaden access and should be approved. Enter ports as comma-separated integers such as `443,8443`; the page accepts up to 20 hosts and 32 ports.
3. Add private CIDRs only for a reviewed need. Private, loopback, link-local, and otherwise unsafe destinations are denied by default. Enable **Allow HTTP** only for an approved non-TLS destination; prefer HTTPS.
4. Choose **Save**. If another user changed the policy, reload the latest version and review it before saving.

## Example

For an approved receiver at `hooks.example.net` over HTTPS, select `tenant-alerts`, add that host, allow port `443`, leave **Allow HTTP** off, and save. Update `tenant-jobs` separately if an HTTP job also needs the host.

## Verify

Reload the policy and confirm its host, port, and HTTP setting. Then use a controlled test request and inspect its safe result code. DNS and network reachability are checked at execution time.

## Recover

If a destination is denied, check the correct policy, exact hostname, effective port, and redirect target. Add only the reviewed destination needed. Do not bypass a block with broad private ranges or unapproved HTTP.

## Related guides

See [webhook sinks](#/help:webhook-sinks), [HTTP jobs](#/help:http-jobs), [secrets](#/help:secrets), and [tenant settings](#/help:settings).
