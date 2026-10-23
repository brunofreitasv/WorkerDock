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
