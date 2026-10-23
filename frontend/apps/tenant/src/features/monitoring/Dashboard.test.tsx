import { render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { Dashboard } from "./Dashboard";

vi.mock("../../api/client", () => ({ read: vi.fn() }));

afterEach(() => vi.clearAllMocks());

test("shows runtime health and host capacity without projection status", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    instanceId: "tenant", observedUtc: "2026-10-23T12:00:00Z", overall: "Healthy", queued: 0, running: 0,
    needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0, runtimeReady: true,
    hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-23T12:00:00Z", upcomingItems: [], recentItems: [],
  } } as never);

  render(<Dashboard language="en" onExecution={vi.fn()} />);

  expect(await screen.findByText("Runtime")).toBeVisible();
  expect(screen.getByText("Ready")).toBeVisible();
  expect(screen.getByText("Host capacity")).toBeVisible();
  expect(screen.queryByText(/projection/i)).not.toBeInTheDocument();
});
