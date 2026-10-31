import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { ManagedJobDetail } from "./ManagedJobDetail";

afterEach(() => vi.unstubAllGlobals());

test("viewers load only execution controls and receive no management actions", async () => {
  const paths: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    paths.push(url.pathname);
    return new Response(JSON.stringify({ limit: 2, pauseOnNeedsReview: true }), { status: 200, headers: { "Content-Type": "application/json" } });
  }));
  render(<ManagedJobDetail jobId="job-1" returnRoute="jobs" t={translations.en} canManage={false} onNavigate={() => {}} />);
  await waitFor(() => expect(screen.getByRole("spinbutton", { name: "Job capacity" })).toHaveValue(2));
  expect(screen.getByRole("switch", { name: "Hold new starts while review is pending" })).toBeChecked();
  expect(screen.getByRole("spinbutton", { name: "Job capacity" })).toBeDisabled();
  expect(screen.getByRole("switch")).toBeDisabled();
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: translations.en.jobEditAction })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: translations.en.runNow })).not.toBeInTheDocument();
  expect(paths).toEqual(["/api/jobs/job-1/execution-controls"]);
});

test("formats history instants in the client zone and labels schedule JSON as wall-time data", async () => {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    const body = url.pathname === "/api/jobs/job-1"
      ? { editor: { revision: { name: "Repeated hour", templateId: "sample", templateVersion: "1" }, enabled: false, archived: false, concurrencyLimit: 1, pauseOnNeedsReview: false }, schedule: { kind: "Once", zoneId: "America/New_York", localOnce: "2026-11-01T01:30:00", selectedOffset: "-05:00" }, missedPolicy: null, schedulePaused: false }
      : url.pathname.endsWith("/executions")
        ? { items: [{ executionId: "run-1", jobRevision: 1, triggerKind: "Scheduled", state: "Succeeded", createdAtUtc: "2026-10-30T03:15:00Z", startedAtUtc: "2026-10-30T03:16:00Z" }], nextCursor: null }
        : { items: [] };
    return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = (await import("@testing-library/user-event")).default.setup();
  render(<ManagedJobDetail jobId="job-1" returnRoute="jobs" t={translations.en} language="en" clientTimeZone="America/Sao_Paulo" canManage onNavigate={() => {}} />);
  await user.click(await screen.findByRole("tab", { name: translations.en.jobSectionSchedule }));
  expect(await screen.findByText(/technical wall-time/i)).toBeVisible();
  expect(screen.getByText(/2026-11-01T01:30:00.*America\/New_York.*-05:00/)).toBeVisible();
  await user.click(screen.getByRole("tab", { name: translations.en.jobSectionHistory }));
  expect(await screen.findByText(/10\/30\/2026, 12:15:00 AM/)).toBeVisible();
});
