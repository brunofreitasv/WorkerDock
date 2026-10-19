# Workload validation

## Status

NOT RUN. No disposable multi-tenant SQL Server deployment and workload host were available. The runner uses WorkerDock's create, manual-enable, run, and execution-detail APIs, but SQL-backed execution and monitoring have not been verified here. Do not present repository tests or a memory adapter as real workload measurements.

The runner accepts only explicitly disposable deployments, requires anonymous management, and defaults to loopback HTTPS targets. It checks `/api/monitoring` and requires its `InstanceId` to match the configured tenant before creating data. It also requires at least one ready worker advertising `workerdock.http@1.0.0`. Set `AllowRemoteHosts` to `true` only for isolated disposable hosts with trusted TLS. Remote fixture URLs also require an exact `AllowedFixtureHosts` entry; wildcards are rejected. Set `FixtureEgressEnforced` to `true` only after configuring tenant egress restrictions for the exact fixture host and port. The runner's DNS address check is best-effort preflight only: it cannot pin or verify the address later resolved by the Worker/Executor, and it does not prevent DNS rebinding. The worker-side egress policy and network controls are the security boundary. Private/reserved fixture addresses require `AllowPrivateFixtureHost=true` and a fixture host matching the tenant host; link-local and metadata addresses remain unsupported. The read-only fixture preflight requires HTTP 200, does not follow redirects, and does not read the response body. It creates uniquely named `workerdock.http@1.0.0` jobs, with one bounded GET to each configured fixture URL per job. The fixture certificate must be trusted by the Worker/Executor, and the fixture destination must be allowed by the tenant egress policy. It warms exactly one run per tenant, measures one run for each remaining job, and polls terminal state and attempt timestamps. It then disables and archives only jobs created by this invocation. If a create response is ambiguous, it searches for the run-scoped unique job name and records it for cleanup; if it cannot reconcile exactly one job, it fails and reports cleanup as uncertain. On an execution timeout it requests a graceful stop; if the job remains active or stop/status cannot be confirmed after 30 seconds, the job is disabled and left unarchived, and the evidence reports its execution ID for operator review. The runner does not delete executions or tenant data. Each job permits one attempt and sends one GET, so retries are measured but not induced by this workload.

This is a fixed-duration HTTP workload. It does not generate mixed job durations, scheduled occurrences, heartbeat delay, or clock-skew measurements. The configured fixture must be a purpose-built bounded endpoint suitable for repeated GET requests. Total concurrent runs across selected tenants are capped at 100. The runner requires PowerShell 7 and a SQL-backed target whose `/api/monitoring/executions/{id}` returns execution details.

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
