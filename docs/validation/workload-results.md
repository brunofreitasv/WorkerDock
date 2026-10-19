# Workload validation

## Status

NOT RUN. No disposable multi-tenant SQL Server deployment and workload host were available for this run. Capacity values in the architecture guide remain test targets, not measured guarantees.

## Required run

Use only disposable tenant databases and bounded test destinations. Run the workload tool described in the release plan after it is implemented and its configuration is populated in protected local storage:

```powershell
pwsh -File tests/load/Invoke-Workload.ps1 `
  -Tenants 10 `
  -JobsPerTenant 100 `
  -ConcurrencyPerTenant 10 `
  -ConfigPath <test-config> `
  -OutputPath <evidence-directory>
```

The script is a planned release deliverable and is not present yet; do not treat this example as an executable command until it is implemented and reviewed.

## Results to record

Record host CPU and memory, SQL Server version and resources, tenant/job/worker counts, job duration mix, concurrency, warm-up and measurement periods, throughput, trigger/queue/execution latency percentiles, retries, failures, timeouts, missed occurrences, worker heartbeat delays, clock skew and artifacts. Report observed limits and invariant violations without extrapolating an SLA.
