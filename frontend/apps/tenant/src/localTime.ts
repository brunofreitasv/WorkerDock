import type { Language } from "@workerdock/ui";

export function getClientTimeZone(): string {
  try {
    const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (typeof timeZone === "string" && timeZone.length > 0) {
      new Intl.DateTimeFormat("en", { timeZone });
      return timeZone;
    }
  } catch {}
  return "UTC";
}

export function formatInstant(
  value: string | Date | null | undefined,
  language: Language,
  timeZone: string,
  kind: "dateTime" | "time" = "dateTime",
): string {
  if (value == null) return "—";
  let date: Date;
  if (value instanceof Date) date = value;
  else {
    if (!/(?:Z|[+-]\d{2}:\d{2})$/i.test(value)) return "—";
    date = new Date(value);
  }
  if (!Number.isFinite(date.getTime())) return "—";
  try {
    return new Intl.DateTimeFormat(language, kind === "time"
      ? { hour: "numeric", minute: "2-digit", second: "2-digit", timeZone }
      : { year: "numeric", month: "2-digit", day: "2-digit", hour: "numeric", minute: "2-digit", second: "2-digit", timeZone }).format(date);
  } catch {
    return "—";
  }
}
