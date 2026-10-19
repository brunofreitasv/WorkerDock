using System.Collections.Concurrent;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Application.Monitoring;

namespace WorkerDock.ControlCenter.Registry;

public sealed class InMemoryCentralRegistry(TimeProvider? clock = null) : ICentralRegistry
{
    private readonly ConcurrentDictionary<Guid, Row> rows = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public ValueTask<Page<TenantDirectoryEntry>> ListAsync(string? search, PageRequest page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); TenantDirectoryCursor.Validate(page);
        var query = rows.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Input.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (TenantDirectoryCursor.Decode(page.Cursor) is { } cursor)
            query = query.Where(x => string.Compare(x.Input.DisplayName, cursor.Name, StringComparison.OrdinalIgnoreCase) > 0 ||
                string.Equals(x.Input.DisplayName, cursor.Name, StringComparison.OrdinalIgnoreCase) && x.Id.CompareTo(cursor.Id) > 0);
        var selected = query.OrderBy(x => x.Input.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).Take(page.Limit + 1).Select(Project).ToArray();
        var hasMore = selected.Length > page.Limit;
        var items = selected.Take(page.Limit).ToArray();
        return ValueTask.FromResult(new Page<TenantDirectoryEntry>(items, hasMore ? TenantDirectoryCursor.Encode(items[^1]) : null));
    }

    public ValueTask<TenantRegistration?> GetAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(rows.TryGetValue(id, out var row) ? new TenantRegistration(row.Id, row.Input.DisplayName, row.Input.ExpectedInstanceId,
            new Uri(row.Input.DashboardUrl), new SecretReference(row.Input.HealthCredentialReference), row.Version.ToArray(), row.CreatedUtc, row.UpdatedUtc) : null);
    }

    public ValueTask<TenantDirectoryEntry> SaveAsync(Guid? id, TenantRegistrationInput proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = TenantRegistrationValidation.Validate(proposed); ValidateActor(actor);
        if (id is null)
        {
            var newId = Guid.NewGuid(); var now = time.GetUtcNow();
            var row = new Row(newId, input, BitConverter.GetBytes(1L), now, now, "Unknown", null, null);
            rows.TryAdd(newId, row);
            return ValueTask.FromResult(Project(row));
        }
        if (expectedVersion is null || !rows.TryGetValue(id.Value, out var current) || !current.Version.SequenceEqual(expectedVersion))
            throw new WorkerDockException("StaleVersion", "headers.ifMatch");
        var revision = BitConverter.ToInt64(current.Version) + 1;
        var updated = current with { Input = input, Version = BitConverter.GetBytes(revision), UpdatedUtc = time.GetUtcNow() };
        if (!rows.TryUpdate(id.Value, updated, current)) throw new WorkerDockException("StaleVersion", "headers.ifMatch");
        return ValueTask.FromResult(Project(updated));
    }

    public ValueTask RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ValidateActor(actor);
        if (!rows.TryGetValue(id, out var current) || !current.Version.SequenceEqual(expectedVersion) || !rows.TryRemove(new KeyValuePair<Guid, Row>(id, current)))
            throw new WorkerDockException("StaleVersion", "headers.ifMatch");
        return ValueTask.CompletedTask;
    }

    public ValueTask<TenantDirectoryEntry> RecordHealthAsync(Guid id, string health, string? safeCode, DateTimeOffset attemptedUtc, DateTimeOffset? succeededUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (health is not ("Healthy" or "Unhealthy" or "IdentityMismatch" or "Unknown")) throw new WorkerDockException("ValidationFailed");
        while (rows.TryGetValue(id, out var current))
        {
            var updated = current with { Health = health, LastAttemptUtc = attemptedUtc, LastSuccessUtc = succeededUtc ?? current.LastSuccessUtc, SafeCode = SafeCode(safeCode) };
            if (rows.TryUpdate(id, updated, current)) return ValueTask.FromResult(Project(updated));
        }
        throw new WorkerDockException("TenantRegistrationNotFound");
    }

    private TenantDirectoryEntry Project(Row row)
    {
        var health = row.Health == "Healthy" && row.LastSuccessUtc < time.GetUtcNow().AddMinutes(-2) ? "Stale" : row.Health;
        return new(row.Id, row.Input.DisplayName, row.Input.ExpectedInstanceId, new Uri(row.Input.DashboardUrl), health, row.LastAttemptUtc, row.LastSuccessUtc, row.Version.ToArray());
    }

    private static void ValidateActor(ActorContext actor)
    {
        if (string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 300 || actor.CorrelationId == Guid.Empty) throw new WorkerDockException("InvalidActor");
    }
    private static string? SafeCode(string? value) => value is not null && value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;
    private sealed record Row(Guid Id, TenantRegistrationInput Input, byte[] Version, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
        string Health, DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastSuccessUtc, string? SafeCode = null);
}
