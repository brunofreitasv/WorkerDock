namespace WorkerDock.Domain.Scheduling;

/// <summary>Provides the deployed timezone-rule fingerprint used to version future schedule projections.</summary>
public interface IScheduleRulesFingerprint
{
    string GetFingerprint(string zoneId);
}
