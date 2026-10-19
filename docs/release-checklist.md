# Release checklist

Use this checklist for a candidate release. A checked source build or portable smoke does not imply that live Windows, IIS, SQL Server, identity-provider, restore, security, or workload validation passed. Record commands, environment versions, results, and artifact locations in the release evidence; do not substitute simulated checks.

## Candidate and packages

- [ ] Select a version and confirm tenant and center package kinds.
- [ ] Build from a clean, reviewed source checkout using locked dependencies.
- [ ] Record package SHA-256 values and migration manifest/checksums.
- [ ] Set tenant and center schema compatibility windows only from tested evidence.
- [ ] Confirm packages and deployment examples contain no secrets or customer data.
- [ ] Review third-party licenses/notices and include required attribution in distributed artifacts.

## Verification

- [ ] Run the required .NET, frontend, and deployment smoke checks; record exact outcomes.
- [ ] Run integration tests against the supported SQL Server version and record schema compatibility.
- [ ] Complete disposable Windows/IIS installation and re-entry checks for tenant, center, and additional worker.
- [ ] Verify HTTPS/SNI, service identities, runtime SQL permissions, Data Protection key ACLs, and archive ACLs.
- [ ] Install and verify production `ISecretVault` adapters in the Control Center and each tenant process that resolves secrets. Confirm identity isolation, reference allowlists, rotation, and redaction; leave `WORKERDOCK_DEVELOPMENT_VAULT` disabled.
- [ ] Exercise Entra/OIDC with a designated test registration, including role-protected management.
- [ ] Verify Anonymous mode is not enabled on any network-exposed deployment.
- [ ] Exercise upgrade, interrupted-upgrade recovery, verified backup, and restore on disposable infrastructure.
- [ ] Complete the fault/security/isolation matrix and accessibility checks required by the release plan.
- [ ] Run the target workload and publish hardware, workload mix, concurrency, throughput, latency, and errors. Measure clock skew and scheduled-work behavior separately; the supplied manual-run load driver does not cover them. The 10 tenant / 100 jobs / 10 concurrent executions target is not a capacity guarantee.

## Operations and handoff

- [ ] Review known limitations: at-least-once delivery, uncertain external effects, infinite-timeout jobs, shared infrastructure administrators, SQL availability dependency, and no strict global priority guarantee.
- [ ] Confirm rollback and restore instructions match the tested schema/application compatibility window.
- [ ] Confirm health, monitoring, alert, retention, and archive failure procedures with operators.
- [ ] Record unresolved blockers and evidence gaps explicitly; do not label them as passed.
- [ ] Prepare release notes and checksums for review.
- [ ] Obtain explicit authorization before publishing or tagging a release; publication is a separate action.
