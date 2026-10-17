# Local development

Install .NET SDK 10.0.402 and restore with `dotnet restore WorkerDock.slnx --locked-mode`. The solution targets .NET 10 and commits per-project package lock files.

Run the independent adapter and host tests without SQL Server:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~InMemoryTenantSettingsTests|FullyQualifiedName~MigrationInputTests|FullyQualifiedName~WebHostTests|FullyQualifiedName~Invalid_database_kind_fails_before_connecting|FullyQualifiedName~Invalid_settings_and_actor_are_rejected_before_connecting'
dotnet build WorkerDock.slnx -c Release
```

`InMemoryTenantSettingsService` implements the same `ITenantSettingsService` port as the SQL adapter. It exercises version checks and atomic settings/audit behavior in tests. It is only selected by the web host when Development and `Tenant:Adapter=Memory` are both set. `/health/live` reports process liveness; `/health/ready` checks the configured tenant instance. The `/_test/settings` route exists only in Development.

To run a disposable memory-backed development instance, set `ASPNETCORE_ENVIRONMENT=Development`, `Tenant__Adapter=Memory`, and `Tenant__InstanceId` to a generated GUID. Run `dotnet run --project src/WorkerDock.Web --urls http://localhost:5000`. `GET http://localhost:5000/_test/settings` returns settings and a quoted `ETag`. `PUT` to the same path with JSON `{"displayName":"Team","displayTimeZone":"UTC"}`, the GET value in `If-Match`, and `X-WorkerDock-CSRF: development` updates it. Validation and stale-version errors use ProblemDetails with stable codes and field paths. The memory state and audit events last only for this process. Bind the development server to localhost.

To run real SQL tests, follow [database.md](database.md). Do not treat the memory test result as proof that SQL scripts or locking work.
