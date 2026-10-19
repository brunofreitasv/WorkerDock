namespace WorkerDock.Domain.Scheduling;

/// <summary>Business recovery behavior for schedule occurrences missed while the scheduler was unavailable.</summary>
public sealed record MissedWorkPolicy(string Mode, TimeSpan LatenessTolerance, int CatchUpCount, TimeSpan CatchUpAge)
{
    public const int MaximumCatchUpCount = 100;
    public static MissedWorkPolicy Default { get; } = new("Skip", TimeSpan.FromSeconds(60), 0, TimeSpan.Zero);

    public IReadOnlyList<DateTimeOffset> SelectOccurrences(
        IEnumerable<DateTimeOffset> candidates,
        DateTimeOffset activatedUtc,
        DateTimeOffset throughUtc)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (activatedUtc.Offset != TimeSpan.Zero || throughUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Occurrence boundaries must be UTC.");
        Validate();

        var due = candidates.Where(value => value.Offset == TimeSpan.Zero && value > activatedUtc && value <= throughUtc)
            .Distinct().Order().ToArray();
        if (due.Length == 0) return Array.Empty<DateTimeOffset>();

        var timelyBoundary = throughUtc - LatenessTolerance;
        var missed = due.Where(value => value < timelyBoundary).ToArray();
        var timely = due.Where(value => value >= timelyBoundary).ToArray();
        var selectedMissed = Mode switch
        {
            "Skip" => Array.Empty<DateTimeOffset>(),
            "Coalesce" => missed.Length == 0 ? Array.Empty<DateTimeOffset>() : [missed[^1]],
            "CatchUp" => missed.Where(value => throughUtc - value <= CatchUpAge)
                .Take(Math.Max(0, CatchUpCount - timely.Length)).ToArray(),
            _ => throw new InvalidOperationException("Unsupported missed-work mode.")
        };
        var selectedTimely = Mode == "CatchUp" ? timely.TakeLast(CatchUpCount).ToArray() : timely;
        return selectedMissed.Concat(selectedTimely).Distinct().Order().ToArray();
    }

    public void Validate()
    {
        if (Mode is not ("Skip" or "Coalesce" or "CatchUp") || LatenessTolerance < TimeSpan.Zero)
            throw new ArgumentException("Invalid missed-work policy.");
        if (Mode == "CatchUp" && (CatchUpCount is < 1 or > MaximumCatchUpCount || CatchUpAge <= TimeSpan.Zero))
            throw new ArgumentException("Catch-up requires a positive bounded count and age.");
        if (Mode != "CatchUp" && (CatchUpCount != 0 || CatchUpAge != TimeSpan.Zero))
            throw new ArgumentException("Catch-up bounds are only valid for CatchUp mode.");
    }
}
