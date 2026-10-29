import { expect, test } from "vitest";
import { formatDuration, toUtcRange, type HistoryPeriod } from "./historyTime";

test("formats TimeSpan durations with days and fractional seconds", () => {
  expect(formatDuration("1.02:03:04.5000000")).toBe("1d 02:03:04.5000000");
  expect(formatDuration("00:03:04")).toBe("00:03:04");
});

const custom = (startDate: string, endDate: string): HistoryPeriod => ({ kind: "custom", startDate, endDate });
const now = new Date("2026-03-10T16:30:00.000Z");

test("converts inclusive custom dates through the browser timezone including DST", () => {
  expect(toUtcRange(custom("2026-03-08", "2026-03-09"), "America/New_York", now)).toEqual({
    fromUtc: "2026-03-08T05:00:00.000Z",
    toUtc: "2026-03-10T04:00:00.000Z",
  });
});

test("presets are rolling windows ending at now", () => {
  expect(toUtcRange({ kind: "24h" }, "America/Los_Angeles", now)).toEqual({
    fromUtc: "2026-03-09T16:30:00.000Z",
    toUtc: "2026-03-10T16:30:00.000Z",
  });
  expect(toUtcRange({ kind: "7d" }, "America/Los_Angeles", now).fromUtc).toBe("2026-03-03T16:30:00.000Z");
  expect(toUtcRange({ kind: "30d" }, "America/Los_Angeles", now).fromUtc).toBe("2026-02-08T16:30:00.000Z");
});

test.each([
  ["not-a-date", "2026-03-09", 30],
  ["2026-03-10", "2026-03-09", 30],
  ["2026-01-01", "2026-03-09", 30],
])("rejects invalid, reversed, or over-limit custom dates (%s through %s)", (startDate, endDate, maxDays) => {
  expect(() => toUtcRange(custom(startDate, endDate), "America/New_York", now, maxDays)).toThrow(RangeError);
});
