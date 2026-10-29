import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { Dashboard } from "./Dashboard";

vi.mock("../../api/client", () => ({ read: vi.fn() }));

afterEach(() => { vi.clearAllMocks(); vi.useRealTimers(); });

test("shows runtime health and host capacity without projection status", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [], recentItems: [],
  } } as never);

  render(<Dashboard language="en" timeZone="UTC" onHistory={vi.fn()} onExecution={vi.fn()} />);

  expect(await screen.findByText("Runtime")).toBeVisible();
  expect(screen.getByText("Ready")).toBeVisible();
  expect(screen.getByText("Host capacity")).toBeVisible();
  expect(screen.queryByText(/projection/i)).not.toBeInTheDocument();
});

test("shows at most ten recent executions in a table with template and duration", async () => {
  const onHistory = vi.fn();
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [],
    recentItems: Array.from({ length: 12 }, (_, index) => ({
      executionId: `execution-${index}`, jobId: `job-${index}`, jobName: `Job ${index}`, state: "Succeeded",
      createdAtUtc: "2026-10-23T12:00:00Z", correlationId: `correlation-${index}`, templateId: "http", duration: "00:01:42",
    })),
  } } as never);

  render(<Dashboard language="en" timeZone="UTC" onHistory={onHistory} onExecution={vi.fn()} />);

  const table = await screen.findByRole("table", { name: "Recent executions" });
  expect(within(table).getAllByRole("row")).toHaveLength(11);
  expect(within(table).getAllByText("http")).toHaveLength(10);
  expect(within(table).getAllByText("00:01:42")).toHaveLength(10);
  expect(within(table).queryByText("Job 10")).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "View all executions" }));
  expect(onHistory).toHaveBeenCalledOnce();
});

test("uses the translated full-history action in Portuguese", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [], recentItems: [],
  } } as never);
  render(<Dashboard language="pt-BR" timeZone="UTC" onHistory={vi.fn()} onExecution={vi.fn()} />);

  expect(await screen.findByRole("button", { name: "Ver todas as execuções" })).toBeVisible();
});

test("formats the updated time in the selected timezone", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-23T12:00:00Z"));
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [], recentItems: [],
  } } as never);

  render(<Dashboard language="pt-BR" timeZone="Pacific/Kiritimati" onHistory={vi.fn()} onExecution={vi.fn()} />);
  await act(async () => { await Promise.resolve(); await Promise.resolve(); });

  expect(screen.getByText("Atualizado: 02:00:00")).toBeVisible();
});

test("keeps days and time in durations over 24 hours", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [],
    recentItems: [{ executionId: "long", jobId: "job", jobName: "Long job", state: "Succeeded", createdAtUtc: "2026-10-23T12:00:00Z", correlationId: "corr", templateId: "http", duration: "1.02:03:04" }],
  } } as never);
  render(<Dashboard language="en" timeZone="UTC" onHistory={vi.fn()} onExecution={vi.fn()} />);
  expect(await screen.findByText("1d 02:03:04")).toBeVisible();
});
