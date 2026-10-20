using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.ControlCenter.Registry;

public sealed class FileCentralRegistry : ICentralRegistry, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CentralJournalStore _journal;
    private readonly CentralHealthSnapshot _health;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, RegistrationState> _registrations = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private FileCentralRegistry(CentralJournalStore journal, CentralHealthSnapshot health, TimeProvider time)
    {
        _journal = journal;
        _health = health;
        _time = time;
        foreach (var record in journal.Records)
        {
            var mutation = record.Mutation;
            if (mutation.Action == "LegacyAudit") continue;
            if (mutation.Action == "Removed") _registrations.Remove(mutation.RegistrationId);
            else if (mutation.After is not null) _registrations[mutation.RegistrationId] = new RegistrationState(mutation.After, record.Sequence);
        }
    }

    public static FileCentralRegistry Open(string dataDirectory, Guid instanceId, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return OpenCore(dataDirectory, instanceId, time, null);
    }

    internal static FileCentralRegistry Open(string dataDirectory, Guid instanceId, TimeProvider time,
        Func<FileStream, ICentralJournalWriter>? writerFactory) => OpenCore(dataDirectory, instanceId, time, writerFactory);

    private static FileCentralRegistry OpenCore(string dataDirectory, Guid instanceId, TimeProvider time,
        Func<FileStream, ICentralJournalWriter>? writerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(time);
        var journal = writerFactory is null ? CentralJournalStore.Open(dataDirectory, instanceId) : CentralJournalStore.Open(dataDirectory, instanceId, writerFactory);
        try { return new FileCentralRegistry(journal, CentralHealthSnapshot.Open(dataDirectory), time); }
        catch { journal.Dispose(); throw; }
    }

    public async ValueTask<Page<TenantDirectoryEntry>> ListAsync(string? search, PageRequest page, CancellationToken ct)
    {
        TenantDirectoryCursor.Validate(page);
        if (search?.Length > 200) throw new WorkerDockException("ValidationFailed", "search");
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!_journal.IsWritable) throw new WorkerDockException("PersistenceUnavailable");
            var query = _registrations.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Snapshot.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
            if (TenantDirectoryCursor.Decode(page.Cursor) is { } cursor)
                query = query.Where(x => string.Compare(x.Snapshot.DisplayName, cursor.Name, StringComparison.OrdinalIgnoreCase) > 0 ||
                    string.Equals(x.Snapshot.DisplayName, cursor.Name, StringComparison.OrdinalIgnoreCase) && x.Snapshot.Id.CompareTo(cursor.Id) > 0);
            var selected = query.OrderBy(x => x.Snapshot.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Snapshot.Id)
                .Take(page.Limit + 1).Select(Project).ToArray();
            var hasMore = selected.Length > page.Limit;
            var items = selected.Take(page.Limit).ToArray();
            return new Page<TenantDirectoryEntry>(items, hasMore ? TenantDirectoryCursor.Encode(items[^1]) : null);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<TenantRegistration?> GetAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return _registrations.TryGetValue(id, out var state) ? ToRegistration(state) : null;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<TenantDirectoryEntry> SaveAsync(Guid? id, TenantRegistrationInput proposed,
        byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        var input = TenantRegistrationValidation.Validate(proposed);
        ValidateActor(actor);
        if (id is null && expectedVersion is not null || id is not null && expectedVersion is null)
            throw new WorkerDockException("ValidationFailed", "headers.ifMatch");

        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            RegistrationState? current = null;
            if (id is not null && (!_registrations.TryGetValue(id.Value, out current) ||
                !Version(current.Revision).AsSpan().SequenceEqual(expectedVersion)))
                throw new WorkerDockException("StaleVersion", "headers.ifMatch");

            var now = _time.GetUtcNow().ToUniversalTime();
            var registrationId = id ?? Guid.NewGuid();
            var after = new TenantRegistrationSnapshot(registrationId, input.DisplayName, input.ExpectedInstanceId,
                input.DashboardUrl, input.HealthCredentialReference, current?.Snapshot.CreatedAtUtc ?? now, now);
            var before = current?.Snapshot;
            var mutation = new CentralJournalMutation(id is null ? "Registered" : "Updated", registrationId, before, after, now, actor,
                JsonSerializer.Serialize(new { before = AuditView(before), after = AuditView(after) }, JsonOptions));

            var revision = await _journal.AppendAsync(mutation, ct);
            var state = new RegistrationState(after, revision);
            _registrations[registrationId] = state;
            return Project(state);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ValidateActor(actor);
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!_registrations.TryGetValue(id, out var current) || !Version(current.Revision).AsSpan().SequenceEqual(expectedVersion))
                throw new WorkerDockException("StaleVersion", "headers.ifMatch");

            var now = _time.GetUtcNow().ToUniversalTime();
            var mutation = new CentralJournalMutation("Removed", id, current.Snapshot, null, now, actor,
                JsonSerializer.Serialize(new { before = AuditView(current.Snapshot), after = (object?)null }, JsonOptions));
            await _journal.AppendAsync(mutation, ct);
            _registrations.Remove(id);
            await _health.RemoveAsync(id, ct);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<TenantDirectoryEntry> RecordHealthAsync(Guid id, string health, string? safeCode,
        DateTimeOffset attemptedUtc, DateTimeOffset? succeededUtc, CancellationToken ct)
    {
        if (health is not ("Healthy" or "Unhealthy" or "IdentityMismatch" or "Unknown") || attemptedUtc.Offset != TimeSpan.Zero ||
            succeededUtc?.Offset != TimeSpan.Zero && succeededUtc is not null)
            throw new WorkerDockException("ValidationFailed");
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!_registrations.TryGetValue(id, out var state)) throw new WorkerDockException("TenantRegistrationNotFound");
            var previous = _health.Get(id);
            var observation = new HealthObservation(health, safeCode, attemptedUtc, succeededUtc ?? previous?.LastSuccessUtc);
            await _health.SaveAsync(id, observation, ct);
            return Project(state);
        }
        finally { _gate.Release(); }
    }

    private TenantDirectoryEntry Project(RegistrationState state)
    {
        var observation = _health.Get(state.Snapshot.Id);
        var health = observation?.Health ?? "Unknown";
        if (health == "Healthy" && observation?.LastSuccessUtc is { } success && success < _time.GetUtcNow().AddMinutes(-2)) health = "Stale";
        return new TenantDirectoryEntry(state.Snapshot.Id, state.Snapshot.DisplayName, state.Snapshot.ExpectedInstanceId,
            new Uri(state.Snapshot.DashboardUrl), health, observation?.LastAttemptUtc, observation?.LastSuccessUtc, Version(state.Revision));
    }

    private static TenantRegistration ToRegistration(RegistrationState state) => new(state.Snapshot.Id, state.Snapshot.DisplayName,
        state.Snapshot.ExpectedInstanceId, new Uri(state.Snapshot.DashboardUrl), new SecretReference(state.Snapshot.HealthCredentialReference),
        Version(state.Revision), state.Snapshot.CreatedAtUtc, state.Snapshot.UpdatedAtUtc);

    private static byte[] Version(long revision) => BitConverter.GetBytes(revision);

    private static object? AuditView(TenantRegistrationSnapshot? snapshot) => snapshot is null ? null : new
    {
        snapshot.Id,
        snapshot.DisplayName,
        snapshot.ExpectedInstanceId,
        snapshot.DashboardUrl,
        snapshot.CreatedAtUtc,
        snapshot.UpdatedAtUtc
    };

    private static void ValidateActor(ActorContext actor)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 300 ||
            string.IsNullOrWhiteSpace(actor.AuthenticationMode) || actor.AuthenticationMode.Length > 32 || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("InvalidActor");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _health.Dispose();
        _journal.Dispose();
        _gate.Dispose();
    }

    private sealed record RegistrationState(TenantRegistrationSnapshot Snapshot, long Revision);
}
