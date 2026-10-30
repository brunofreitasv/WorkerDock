import { render, screen, within } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { Dashboard } from "./Dashboard";
import { WorkersPage } from "./WorkersPage";
import { JobsTable } from "./JobsTable";
import { ExecutionDetails } from "./ExecutionDetails";

vi.mock("../../api/client", () => ({ read: vi.fn() }));
afterEach(() => vi.clearAllMocks());

test("puts current health and open work before history and upcoming sections", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-29T12:00:00Z", overall: "Healthy", queued: 2, running: 1,
    needsReview: 3, succeeded24h: 4, failed24h: 5, longRunning: 0, upcoming: 1, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-29T12:00:00Z", recentItems: [], upcomingItems: [],
  } } as never);
  render(<Dashboard language="en" timeZone="UTC" onHistory={vi.fn()} onExecution={vi.fn()} />);
  const sections = await screen.findAllByRole("region");
  expect(sections.map(section => section.getAttribute("aria-label"))).toEqual([
    "Current health", "Open items", "24-hour activity", "Recent executions", "Upcoming work",
  ]);
  expect(within(sections[0]).getByText("Healthy")).toBeVisible();
  expect(within(sections[1]).getByText("Needs review")).toBeVisible();
  expect(within(sections[3]).getByRole("button", { name: "View all executions" })).toBeVisible();
});

test("keeps runtime state separate from its identity and diagnostics", async () => {
  vi.mocked(read).mockResolvedValue({ data: { bootId: "boot-a", ready: true, hostCeiling: 4, lastHeartbeatUtc: "2026-10-29T12:00:00Z" } } as never);
  render(<WorkersPage language="en" />);
  expect(await screen.findByRole("region", { name: "Runtime health" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Runtime details" })).toBeVisible();
  expect(screen.getByText("boot-a")).toBeVisible();
});

test("separates job filters from monitoring results while keeping read access", async () => {
  vi.mocked(read).mockResolvedValue({ data: { items: [], nextCursor: null } } as never);
  render(<JobsTable language="en" />);
  expect(await screen.findByRole("region", { name: "Jobs" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Filters" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Refresh" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "History" })).not.toBeInTheDocument();
});

test("groups execution identity, diagnostics and review access", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    executionId: "execution-1", jobId: "job-1", jobName: "Nightly import", templateId: "sql", templateVersion: "1",
    jobRevision: 2, state: "NeedsReview", certainty: "Unknown", triggerKind: "Manual", createdAtUtc: "2026-10-29T12:00:00Z",
    availableAtUtc: "2026-10-29T12:00:00Z", scheduledForUtc: null, correlationId: "corr-1", stopRequested: false,
    forceStop: false, attempts: [], timeline: [],
  } } as never);
  render(<ExecutionDetails executionId="execution-1" language="en" />);
  expect(await screen.findByRole("region", { name: "Execution overview" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Attempts" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Timeline" })).toBeVisible();
  expect(screen.getByRole("region", { name: "Review" })).toBeVisible();
  expect(screen.queryByRole("button", { name: /retry|cancel|reconcile/i })).not.toBeInTheDocument();
});
