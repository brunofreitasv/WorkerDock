using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace WorkerDock.Domain.Scheduling;

public sealed class SystemScheduleRulesFingerprint : IScheduleRulesFingerprint
{
    public string GetFingerprint(string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var rules = JsonSerializer.Serialize(zone.GetAdjustmentRules().Select(rule => new
        {
            rule.DateStart,
            rule.DateEnd,
            rule.DaylightDelta,
            rule.DaylightTransitionStart,
            rule.DaylightTransitionEnd
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(zone.Id + "|" + zone.BaseUtcOffset + "|" + rules)));
    }
}
