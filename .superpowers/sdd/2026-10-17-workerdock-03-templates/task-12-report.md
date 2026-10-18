# Task 12 — destination-safe HTTP handler

Base: `0c79da2`.

Implemented `workerdock.http@1.0.0` as an independent template project, registered it in Worker and Executor, and added safe HTTP configuration/substitution, bounded request and response handling, secret-header resolution, outer-deadline semantics, conservative retry classification, and bounded `Retry-After` reporting without handler retries. The outbound policy normalizes host/IP forms, rejects prohibited destinations and mixed DNS answers, pins each connection to an approved address, preserves the original host for TLS/SNI, disables proxy and redirects, and verifies the connected peer. Production loopback remains denied; local server tests use an explicitly injected test policy. Added HTTP and outbound policy documentation.

## RED/GREEN and verification

- Initial focused RED: the new HTTP/outbound-policy tests could not compile before their implementation (`CS0234`/`CS0246` for the missing template and outbound policy types).
- Focused HTTP/outbound tests: 28 passed, 0 failed. Includes controlled local server and DNS rebinding proof (the late forbidden answer received no socket), mixed-answer denial, IPv4/IPv6/mapped ranges, normalized hosts, metadata/private/link-local/loopback, userinfo, TLS/SNI, redirects/proxy, response bounds, header injection, secrets, timeout/deadline behavior, and retry classification.
- SQL-independent integration suite: 169 passed, 0 failed.
- Unit suite: 31 passed, 0 failed.
- `dotnet build WorkerDock.slnx -c Release --no-restore`: succeeded, 0 warnings, 0 errors.
- Process suite: 32 passed, 1 failed in two full runs, both at `ContainmentTests.Disposing_active_noncooperative_child_confirms_tree_exit_without_lifecycle_race` with `ExecutorStartupFailed` at `ExecutionSupervisor.cs:93`. The exact test passed isolated (1/1). It launches `FixtureProgram` through `FixtureProgram.Options()` and does not use production Worker/Executor composition or the HTTP template; the wrapped startup exception hides the underlying cause. No unrelated Runtime change was made; this remains an unexplained process-stress concern.
- SQL-backed integration tests remain `SQL_TEST_BLOCKED`: required `WORKERDOCK_TEST_SQL`, `WORKERDOCK_TEST_SQL_RUNTIME`, and `WORKERDOCK_TEST_SQL_TARGET_RUNTIME` configuration is unavailable. This does not block the local HTTP tests and is not counted as an HTTP failure.

## Scope review

Only Task12 HTTP project, outbound policy and connection factory, Worker/Executor registrations, solution/project references and lockfiles, focused integration tests, and the two requested docs are included. Task12A and Task13 were not implemented. `git diff --check` passes.
