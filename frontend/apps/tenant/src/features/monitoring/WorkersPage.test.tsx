import { render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { WorkersPage } from "./WorkersPage";

vi.mock("../../api/client", () => ({ read: vi.fn() }));

afterEach(() => vi.clearAllMocks());

test("shows one runtime status and host capacity", async () => {
  vi.mocked(read).mockResolvedValue({ data: {
    bootId: "runtime-boot", ready: true, hostCeiling: 4, lastHeartbeatUtc: "2026-10-23T12:00:00Z",
  } } as never);

  render(<WorkersPage language="en" />);

  expect(await screen.findByRole("table", { name: "Runtime" })).toBeVisible();
  expect(screen.getByText("runtime-boot")).toBeVisible();
  expect(screen.getByText("4")).toBeVisible();
  expect(screen.queryByText("Worker ID")).not.toBeInTheDocument();
});

test("formats heartbeat using the supplied client timezone", async () => {
  vi.mocked(read).mockResolvedValue({ data: { bootId: "boot", ready: true, hostCeiling: 4, lastHeartbeatUtc: "2026-10-30T03:15:00Z" } } as never);
  render(<WorkersPage language="en" timeZone="America/Sao_Paulo" />);
  expect(await screen.findByText("10/30/2026, 12:15:00 AM")).toBeVisible();
});
