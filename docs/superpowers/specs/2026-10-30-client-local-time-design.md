# Client-local time design

## Intent

An operator in GMT-3 who creates a job at 00:15 currently sees 03:15 on screens that format its UTC timestamp using the session's `UTC` zone. All operational dates and times in the tenant UI should reflect the client machine's timezone, with the existing language choice controlling date and time syntax.

## Current behavior and cause

`/api/session` always returns `timeZone: "UTC"`. `usePreferences` adopts that value unless a local override exists. Dashboard, history, audit, and alert views pass the resulting zone to `Intl`, while execution details and worker heartbeat use the browser zone and managed job history shows raw UTC strings. Schedule previews show raw UTC and offset. These different paths explain the inconsistent UI; there is no evidence yet that stored UTC instants are wrong.

## Goals

- Display every operational instant in the client machine's timezone: creation, scheduled occurrence, availability, start, end, deadline, heartbeat, audit, alerts, and refresh time where shown.
- Use the same client zone to turn custom calendar-day filters into UTC query ranges.
- Make newly created schedules default to the client zone, while preserving the explicitly configured zone of existing schedules and its execution semantics.
- Make schedule previews understandable in client time. Label the schedule zone when it differs from the client zone; retain the UTC instant as secondary technical detail where useful.
- Remove personal and tenant display-timezone controls so neither a stored override nor a misleading tenant setting can supersede client time. Keep the server's persisted `displayTimeZone` field and request contract temporarily for compatibility; editing the tenant name sends its existing value unchanged.

## Non-goals and constraints

- Do not rewrite stored timestamps or alter scheduler evaluation, recurrence rules, selected offsets, or dynamic-value reference-date semantics.
- API instants remain UTC with an explicit `Z` or numeric offset. Date-only values and schedule-local wall times are distinct types and must not be parsed as UTC instants.
- Continue supporting English and Brazilian Portuguese formatting. Never apply a fixed `-03:00` offset; client zones may observe daylight saving time.
- The client timezone is detected from the browser. A machine timezone change during an open session takes effect after reload; no live OS timezone listener is required.

## Approach

Chosen: automatic client timezone for operational displays and filters, with no UI display override. A shared frontend formatter accepts an offset-bearing instant and a language and uses browser-local `Intl` formatting; null or invalid values render a safe placeholder. A shared client-zone resolver supplies an IANA zone to the existing Temporal-based history filter logic and to defaults for new schedules. The schedule's own zone remains visible and authoritative for cron and one-time input. One-time wall-time fields are explicitly labelled with that zone.

Alternatives considered: (1) keep the existing personal or tenant override with client timezone as the default; this can reproduce the reported 3-hour mismatch from saved preferences and contradicts the requested machine-local rule. (2) convert timestamps on the server; the server cannot reliably know each browser's timezone and would make UTC API contracts ambiguous.

## UI and data flow

The API continues to return UTC instants. The browser resolves its local zone once per page load, and all display components use the same formatter or receive that resolved zone. History filters calculate local day boundaries in that zone before sending UTC boundaries to the API. New schedule forms start with the client zone; editing an existing schedule keeps its saved zone. A preview presents the occurrence in client time, plus the schedule-zone time when different. Raw schedule JSON in the managed job view is replaced or accompanied by a readable summary so the displayed occurrence is not mistaken for client-local time.

Personal Settings shows the detected timezone as read-only information. General tenant settings no longer offers a display-timezone selector. Existing `workerdock.timeZone` local storage is ignored and may be removed; it never changes the UI's zone. The session's UTC field and the persisted tenant setting are retained only as compatibility fields, not display inputs.

## Verification

- With browser zone `America/Sao_Paulo`, `2026-10-30T03:15:00Z` displays as October 30, 00:15; `2026-10-30T01:15:00Z` displays as October 29, 22:15.
- A saved `workerdock.timeZone=UTC` and a session `timeZone=UTC` do not override the browser zone.
- Calendar-day filters use midnight-to-midnight in the browser zone, including a daylight-saving transition; relative 24-hour filters remain rolling 24-hour windows.
- Existing schedules keep their zone and selected offset. New schedules default to the browser zone. Previews explain both client time and schedule time when they differ.
- Audit each operational timestamp surface and check two browser zones, null/invalid values, language formatting, and API UTC round trips. Run unit, component, and targeted Playwright tests.
