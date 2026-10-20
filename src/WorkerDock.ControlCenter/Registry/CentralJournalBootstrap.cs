using System.Security.Cryptography;
using System.Text.Json;
using WorkerDock.Application.Common;

namespace WorkerDock.ControlCenter.Registry;

public sealed record LegacyRegistration(Guid Id, string DisplayName, Guid ExpectedInstanceId, string DashboardUrl,
    string HealthCredentialReference, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record LegacyAuditEvent(Guid Id, DateTimeOffset AtUtc, string ActorId, string AuthenticationMode,
    string Action, string? EntityType, string? EntityId, Guid CorrelationId, string SafeDetailsJson);

public sealed record LegacyHealthObservation(Guid RegistrationId, string Health, string? SafeCode,
    DateTimeOffset LastAttemptUtc, DateTimeOffset? LastSuccessUtc);

public sealed record ImportReceipt(int RegistrationCount, int AuditEventCount, int HealthObservationCount,
    string JournalSha256, string HealthSnapshotSha256, Guid InstanceId);

public static class CentralJournalBootstrap
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ImportReceipt> ImportAsync(string dataDirectory, Guid instanceId,
        IReadOnlyList<LegacyRegistration> registrations, IReadOnlyList<LegacyAuditEvent> audit,
        IReadOnlyList<LegacyHealthObservation> health, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("Data directory must be absolute.", nameof(dataDirectory));
        if (instanceId == Guid.Empty) throw new ArgumentException("Center instance ID is required.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(health);
        ct.ThrowIfCancellationRequested();

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (File.Exists(target)) throw new InvalidOperationException("Import target is not a directory.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException("Import target must be empty.");

        var validatedRegistrations = ValidateRegistrations(registrations);
        var validatedAudit = ValidateAudit(audit, validatedRegistrations);
        var validatedHealth = ValidateHealth(health, validatedRegistrations);

        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Import target has no parent directory.", nameof(dataDirectory));
        Directory.CreateDirectory(parent);
        var staging = target + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            using (var journal = CentralJournalStore.Open(staging, instanceId))
            {
                foreach (var registration in validatedRegistrations)
                {
                    ct.ThrowIfCancellationRequested();
                    var snapshot = ToSnapshot(registration);
                    var actor = new ActorContext("legacy-import", "Migration", Guid.NewGuid());
                    var details = JsonSerializer.Serialize(new
                    {
                        registration.Id,
                        registration.DisplayName,
                        registration.ExpectedInstanceId,
                        registration.DashboardUrl,
                        registration.CreatedAtUtc,
                        registration.UpdatedAtUtc
                    }, JsonOptions);
                    await journal.AppendAsync(new CentralJournalMutation("ImportedRegistration", registration.Id,
                        null, snapshot, registration.UpdatedAtUtc, actor, details), ct);
                }

                foreach (var item in validatedAudit)
                {
                    ct.ThrowIfCancellationRequested();
                    var actor = new ActorContext(item.ActorId, item.AuthenticationMode, item.CorrelationId);
                    using var safeDetails = JsonDocument.Parse(item.SafeDetailsJson);
                    var details = JsonSerializer.Serialize(new
                    {
                        eventId = item.Id,
                        action = item.Action,
                        entityType = item.EntityType,
                        entityId = item.EntityId,
                        details = safeDetails.RootElement
                    }, JsonOptions);
                    await journal.AppendAsync(new CentralJournalMutation("LegacyAudit", item.Id, null, null,
                        item.AtUtc, actor, details), ct);
                }
            }

            var healthSnapshot = CentralHealthSnapshot.Open(staging);
            foreach (var item in validatedHealth)
            {
                ct.ThrowIfCancellationRequested();
                await healthSnapshot.SaveAsync(item.RegistrationId,
                    new HealthObservation(item.Health, item.SafeCode, item.LastAttemptUtc, item.LastSuccessUtc), ct);
            }

            using (var verified = CentralJournalStore.Open(staging, instanceId))
            {
                if (verified.Records.Count != validatedRegistrations.Length + validatedAudit.Length)
                    throw new InvalidDataException("Imported journal verification failed.");
            }

            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(staging, "registry.journal"), ct)));
            var healthPath = Path.Combine(staging, "health.json");
            var healthBytes = File.Exists(healthPath) ? await File.ReadAllBytesAsync(healthPath, ct) : Array.Empty<byte>();
            var healthHash = Convert.ToHexString(SHA256.HashData(healthBytes));
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                    throw new InvalidOperationException("Import target must remain empty until cutover.");
                Directory.Delete(target);
            }
            Directory.Move(staging, target);
            return new ImportReceipt(validatedRegistrations.Length, validatedAudit.Length, validatedHealth.Length, hash, healthHash, instanceId);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static LegacyRegistration[] ValidateRegistrations(IReadOnlyList<LegacyRegistration> registrations)
    {
        var ids = new HashSet<Guid>();
        var result = new LegacyRegistration[registrations.Count];
        for (var index = 0; index < registrations.Count; index++)
        {
            var item = registrations[index] ?? throw new ArgumentException("Legacy registration is null.", nameof(registrations));
            if (item.Id == Guid.Empty || !ids.Add(item.Id)) throw new ArgumentException("Legacy registration IDs must be unique and nonempty.", nameof(registrations));
            if (item.CreatedAtUtc.Offset != TimeSpan.Zero || item.UpdatedAtUtc.Offset != TimeSpan.Zero || item.UpdatedAtUtc < item.CreatedAtUtc)
                throw new ArgumentException("Legacy registration timestamps are invalid.", nameof(registrations));
            var valid = TenantRegistrationValidation.Validate(new TenantRegistrationInput(item.DisplayName, item.ExpectedInstanceId,
                item.DashboardUrl, item.HealthCredentialReference));
            result[index] = item with
            {
                DisplayName = valid.DisplayName,
                DashboardUrl = valid.DashboardUrl,
                HealthCredentialReference = valid.HealthCredentialReference
            };
        }
        return result;
    }

    private static LegacyAuditEvent[] ValidateAudit(IReadOnlyList<LegacyAuditEvent> audit, IReadOnlyList<LegacyRegistration> registrations)
    {
        var ids = new HashSet<Guid>();
        var credentialReferences = registrations.Select(item => item.HealthCredentialReference).ToArray();
        var result = new LegacyAuditEvent[audit.Count];
        for (var index = 0; index < audit.Count; index++)
        {
            var item = audit[index] ?? throw new ArgumentException("Legacy audit event is null.", nameof(audit));
            if (item.Id == Guid.Empty || !ids.Add(item.Id) || item.CorrelationId == Guid.Empty || item.AtUtc.Offset != TimeSpan.Zero ||
                string.IsNullOrWhiteSpace(item.ActorId) || item.ActorId.Length > 300 ||
                string.IsNullOrWhiteSpace(item.AuthenticationMode) || item.AuthenticationMode.Length > 32 ||
                string.IsNullOrWhiteSpace(item.Action) || item.Action.Length > 64 ||
                item.EntityType?.Length > 64 || item.EntityId?.Length > 128)
                throw new ArgumentException("Legacy audit metadata is invalid.", nameof(audit));
            if (item.SafeDetailsJson is null || item.SafeDetailsJson.Length > 2000)
                throw new ArgumentException("Legacy audit details are invalid.", nameof(audit));
            try
            {
                using var json = JsonDocument.Parse(item.SafeDetailsJson, new JsonDocumentOptions { MaxDepth = 16 });
                if (json.RootElement.ValueKind != JsonValueKind.Object || ContainsSensitiveField(json.RootElement) ||
                    credentialReferences.Any(reference => item.SafeDetailsJson.Contains(reference, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Legacy audit details are not safe to import.", nameof(audit));
            }
            catch (JsonException error)
            {
                throw new ArgumentException("Legacy audit details are invalid JSON.", nameof(audit), error);
            }
            result[index] = item;
        }
        return result;
    }

    private static LegacyHealthObservation[] ValidateHealth(IReadOnlyList<LegacyHealthObservation> health,
        IReadOnlyList<LegacyRegistration> registrations)
    {
        var registrationIds = registrations.Select(item => item.Id).ToHashSet();
        var latest = new Dictionary<Guid, LegacyHealthObservation>();
        foreach (var item in health)
        {
            if (item is null || item.RegistrationId == Guid.Empty || !registrationIds.Contains(item.RegistrationId) ||
                item.LastAttemptUtc.Offset != TimeSpan.Zero || item.LastSuccessUtc?.Offset != TimeSpan.Zero && item.LastSuccessUtc is not null ||
                item.LastSuccessUtc > item.LastAttemptUtc)
                throw new ArgumentException("Legacy health observation is invalid.", nameof(health));
            var normalizedHealth = item.Health switch
            {
                "Unknown" or "Healthy" or "Unhealthy" or "IdentityMismatch" => item.Health,
                "Stale" => "Healthy",
                _ => throw new ArgumentException("Legacy health status is invalid.", nameof(health))
            };
            var normalized = item with { Health = normalizedHealth, SafeCode = SafeSafeCode(item.SafeCode) };
            if (!latest.TryGetValue(item.RegistrationId, out var prior) || item.LastAttemptUtc >= prior.LastAttemptUtc)
                latest[item.RegistrationId] = normalized;
        }
        return latest.Values.OrderBy(item => item.RegistrationId).ToArray();
    }

    private static bool ContainsSensitiveField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("apikey", StringComparison.OrdinalIgnoreCase) || ContainsSensitiveField(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsSensitiveField);
        }
        return false;
    }

    private static TenantRegistrationSnapshot ToSnapshot(LegacyRegistration item) => new(item.Id, item.DisplayName,
        item.ExpectedInstanceId, item.DashboardUrl, item.HealthCredentialReference, item.CreatedAtUtc, item.UpdatedAtUtc);

    private static string? SafeSafeCode(string? value) => value is not null && value.Length <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? value : null;
}
