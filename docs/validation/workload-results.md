# Workload validation

## Status

**NOT RUN:** No disposable multi-tenant SQL Server deployment and workload host were available. This page is the procedure and evidence form, not a measured result. Repository tests and the memory demo cannot supply workload numbers.

## Before you run it

- Use only explicitly disposable SQL-backed tenants. The runner requires anonymous management and PowerShell 7.
- Prefer loopback HTTPS targets. Use `AllowRemoteHosts=true` only for isolated disposable hosts with trusted TLS.
- Give each remote fixture an exact `AllowedFixtureHosts` entry; wildcards are rejected. Set `FixtureEgressEnforced=true` only after configuring tenant egress for the exact host and port.
- The runner's DNS preflight is best effort. Worker/Executor egress rules and network controls must still enforce the destination. Private or reserved fixture addresses require `AllowPrivateFixtureHost=true` and a fixture host matching the tenant host. Link-local and metadata addresses remain unsupported.
- Use a bounded test endpoint that safely accepts repeated GET requests. Its certificate must be trusted by Worker/Executor. The read-only preflight requires HTTP 200, follows no redirects, and reads no response body.
- The runner checks each tenant's `/api/monitoring` instance ID and requires a ready worker advertising `workerdock.http@1.1.0` before it creates jobs.

## What the runner does

1. Create uniquely named HTTP jobs for one GET to the configured fixture. Each job allows one attempt.
2. Warm one run per tenant, then measure one run for each remaining job. Tenant batches respect `ConcurrencyPerTenant`; total concurrent runs across tenants are capped at 100.
3. Poll execution detail and attempt timestamps. A timeout requests a graceful stop and waits 30 seconds. If work is still active or status cannot be confirmed, leave the job disabled and unarchived and report its execution ID.
4. Disable and archive only jobs created by this invocation. If a create response is ambiguous, search by the unique run-scoped name. If exactly one job cannot be reconciled, report uncertain cleanup and fail. The runner never deletes execution or tenant data.

This fixed-duration manual-run workload does not induce retries, mixed job durations, scheduled occurrences, heartbeat-delay measurements, or clock-skew measurements. Report those separately. SQL-backed execution and monitoring need actual verification before results can be claimed.

## Configuration schema

Version 1 is JSON with this shape:

```json
{
  "schemaVersion": 1,
  "Environment": "disposable",
  "AllowAnonymousManagement": true,
  "AllowRemoteHosts": false,
  "AllowedFixtureHosts": [],
  "AllowPrivateFixtureHost": false,
  "FixtureEgressEnforced": true,
  "SqlServerVersion": "SQL Server 2022",
  "SqlServerResources": { "vCpu": 8, "memoryGiB": 32 },
  "Tenants": [
    {
      "Name": "tenant-01",
      "InstanceId": "00000000-0000-0000-0000-000000000001",
      "BaseUrl": "https://localhost:5001",
      "FixtureUrl": "https://localhost:5443/health"
    }
  ]
}
```

The JSON above shows one entry to explain the shape. Provide at least as many tenant entries as the `-Tenants` argument, with unique names and instance IDs. `FixtureUrl` must be a bounded disposable test endpoint with no credentials, query, or fragment. The runner does not accept OIDC or fixture credentials. Keep this configuration outside the evidence output directory. Supply SQL Server version and resource counts as non-secret metadata. The evidence includes host facts, SQL Server version/resources, discovered worker counts, run counts, concurrency, latency percentiles, states, attempt safe codes, and invariant violations; it omits configured URLs and secrets.

## Required run

```powershell
pwsh -File tests/load/Invoke-Workload.ps1 `
  -Tenants 10 `
  -JobsPerTenant 100 `
  -ConcurrencyPerTenant 10 `
  -ConfigPath '<protected-test-config>' `
  -OutputPath '<existing-evidence-directory>'
```

One run per tenant is excluded as warmup. The remaining jobs are submitted in batches limited by `ConcurrencyPerTenant`; tenants run concurrently. A JSON evidence file is written to `OutputPath`. Non-success executions, retries, missing timestamps, incorrect run counts, timeouts, or cleanup failures cause a nonzero exit after evidence is written.

## Results to record

Record SQL Server resources alongside the configured version and host CPU/memory in the evidence. Results include trigger, API submission, queue, execution, and end-to-end latency percentiles; throughput counts only successful measured executions. Retries, failures, timeouts, and safe attempt codes are reported. Missed schedules are not applicable to these manual runs. Worker heartbeat delays and clock skew are not measured. Report observed limits and invariant violations without extrapolating an SLA.
