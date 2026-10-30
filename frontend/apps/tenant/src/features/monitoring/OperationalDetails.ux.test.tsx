import { render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { JobsTable } from "./JobsTable";
import { ExecutionDetails } from "./ExecutionDetails";

vi.mock("../../api/client", () => ({ read: vi.fn() }));
afterEach(() => vi.clearAllMocks());

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
