# Release fault and security matrix

Use this matrix with the [release checklist](release-checklist.md). For **each candidate**, record a real result and artifact in [release evidence](release-evidence.md). No row is passed merely because a unit test or memory demo passed.

| What can go wrong | Check on the candidate | Evidence to record |
| --- | --- | --- |
| A runtime crashes after external work may have started | Run SQL-backed recovery and uncertain-outcome tests; inspect attempt history and review holds. | SQL test output and the observed recovery state. |
| A child process hangs or ignores stop | Run Windows process tests and a disposable host stop/drain exercise. | Process test output and child-process cleanup observation. |
| Tenant or job capacity is exceeded | Run capacity tests, then measure bounded concurrent work on a SQL-backed tenant. | Limits configured, observed child count, and queued work. |
| A secret appears in logs or results | Run secret-boundary tests and test the installed production vault adapter with synthetic credentials. | Redacted output and adapter/identity configuration evidence. |
| An HTTP job reaches an unapproved address | Run outbound-policy tests and verify deployment egress rules for the tenant. | Denied destinations, allowed hosts, and network-policy evidence. |
| One tenant gains another tenant's access | Test SQL identities, vault scopes, web roles, and Control Center links across two disposable tenants. | Direct API denials and cross-tenant access results. |
| Archive storage fails during retention | Fill or deny a disposable archive target and confirm source rows remain. | Before/after SQL counts and archive error state. |
| Sign-in or management policy is wrong | Run local access-policy tests and a live Entra/IIS role matrix. | Issuer, role, 401/403, callback, and sign-out results. |
| A migration or restore loses data | Apply the manifest through the current target, test guarded rollback, and restore a verified backup. | Schema history, checksums, backup, and restore results. |
| A deployment stops halfway | Interrupt a disposable install/upgrade and reconcile or restore it. | Versions, service state, recovery steps, and readiness. |
| The UI is unusable by keyboard or screen reader | Run browser accessibility checks and a manual assistive-technology review. | Browser output and manual findings. |
| The target workload overloads a tenant | Run the [workload procedure](workload-results.md) on disposable SQL-backed infrastructure. | Hardware, latency, throughput, failures, and cleanup results. |

Portable checks can be run with [developer checks](development-checks.md). SQL, Windows/IIS, Entra, restore, and workload rows need their own disposable environments. Mark unavailable checks **NOT RUN**, not **PASS**.
