import { Temporal } from "@js-temporal/polyfill";

export type HistoryPeriod = { kind: "24h" | "7d" | "30d" } | { kind: "custom"; startDate: string; endDate: string };

export function formatDuration(value: string | null | undefined): string {
  if (!value) return "—";
  const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(\.\d+)?$/.exec(value);
  if (!match) return value;
  const [, days, hours, minutes, seconds, fraction] = match;
  return `${days ? `${days}d ` : ""}${hours}:${minutes}:${seconds}${fraction ?? ""}`;
}

const DAY_MS = 24 * 60 * 60 * 1000;

function localMidnight(dateText: string, timeZone: string): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(dateText)) throw new RangeError("Invalid calendar date");
  try {
    const date = Temporal.PlainDate.from(dateText, { overflow: "reject" });
    return date.toZonedDateTime({ timeZone, plainTime: "00:00" }).toInstant().toString({ fractionalSecondDigits: 3 });
  } catch {
    throw new RangeError("Invalid calendar date or time zone");
  }
}

export function toUtcRange(period: HistoryPeriod, timeZone: string, now: Date, maxDays = 30): { fromUtc: string; toUtc: string } {
  if (period.kind !== "custom") {
    const days = period.kind === "24h" ? 1 : period.kind === "7d" ? 7 : 30;
    return { fromUtc: new Date(now.getTime() - days * DAY_MS).toISOString(), toUtc: now.toISOString() };
  }

  const start = Temporal.PlainDate.from(period.startDate, { overflow: "reject" });
  const end = Temporal.PlainDate.from(period.endDate, { overflow: "reject" });
  if (Temporal.PlainDate.compare(start, end) > 0) throw new RangeError("Start date must not follow end date");
  if (end.since(start).total({ unit: "day" }) + 1 > maxDays) throw new RangeError("Date range exceeds maximum");
  return {
    fromUtc: localMidnight(period.startDate, timeZone),
    toUtc: localMidnight(end.add({ days: 1 }).toString(), timeZone),
  };
}
