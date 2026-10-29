import { afterEach, expect, test, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { read } from "../../api/client";
import { JobsTable } from "./JobsTable";

vi.mock("../../api/client", () => ({ read: vi.fn() }));
afterEach(() => vi.clearAllMocks());
const page = { items: [{ jobId: "job-1", name: "Job one", templateId: "template.test", templateVersion: "1.0", archived: false, enabled: true, activeExecutions: 0, failed24h: 0, hasSchedule: false, schedulePaused: false }], nextCursor: null };

test("shows per-job history only for managers", async () => {
  vi.mocked(read).mockResolvedValue({ data: page, etag: null } as never);
  const readView = render(<JobsTable language="en" />);
  expect(await screen.findByRole("table", { name: "Jobs" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "History" })).not.toBeInTheDocument();
  readView.unmount();

  render(<JobsTable language="en" canManage onHistory={() => undefined} />);
  expect(await screen.findByRole("button", { name: "History" })).toBeVisible();
});
