import { afterEach, expect, test, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { read } from "../../api/client";
import { toUtcRange } from "../monitoring/historyTime";
import { JobHistoryTable } from "./JobHistoryTable";

vi.mock("../../api/client", () => ({ read: vi.fn() }));

afterEach(() => { vi.clearAllMocks(); vi.useRealTimers(); });

const execution = (executionId: string, state: string) => ({
  executionId, jobName: "job one", templateId: "template.test", triggerKind: "Schedule", state,
  createdAtUtc: "2026-10-28T12:00:00Z", startedAtUtc: "2026-10-28T12:00:02Z", duration: "00:00:08",
});

function response(items: ReturnType<typeof execution>[], nextCursor: string | null) {
  return { data: { items, nextCursor }, etag: null } as never;
}
function url(path: string) { return new URL(path, window.location.origin); }

test("filters date and state, replaces the page, and links to execution details", async () => {
  vi.setSystemTime(new Date("2026-10-29T12:00:00Z"));
  const expectedDefault = toUtcRange({ kind: "7d" }, "UTC", new Date(), 90);
  const user = userEvent.setup();
  const requests: string[] = [];
  vi.mocked(read).mockImplementation(async (path: string) => {
    requests.push(path);
    const request = url(path);
    return request.searchParams.get("cursor") === "cursor-2"
      ? response([execution("execution-2", "Failed")], null)
      : response([execution("execution-1", "Succeeded")], "cursor-2");
  });

  render(<JobHistoryTable jobId="job-1" language="en" timeZone="UTC" />);
  const table = await screen.findByRole("table", { name: "Job execution history" });
  expect(screen.getByRole("heading", { level: 2, name: "Job history · job-1" })).toBeVisible();
  expect(within(table).getByText("Succeeded")).toBeVisible();
  expect(within(table).getByText("template.test")).toBeVisible();
  expect(within(table).getByRole("link", { name: "execution-1" })).toHaveAttribute("href", "#/execution:execution-1");
  const initial = url(requests[0]);
  expect(initial.pathname).toBe("/api/monitoring/jobs/job-1/executions");
  expect(initial.searchParams.get("limit")).toBe("50");
  expect(initial.searchParams.get("fromUtc")).toBe(expectedDefault.fromUtc);
  expect(initial.searchParams.get("toUtc")).toBe(expectedDefault.toUtc);

  await user.click(screen.getByRole("button", { name: "Next" }));
  await waitFor(() => expect(within(screen.getByRole("table", { name: "Job execution history" })).getByText("Failed")).toBeVisible());
  expect(screen.queryByRole("link", { name: "execution-1" })).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Custom" }));
  fireEvent.change(screen.getByLabelText("Start date"), { target: { value: "2026-10-01" } });
  fireEvent.change(screen.getByLabelText("End date"), { target: { value: "2026-10-02" } });
  await user.click(screen.getByRole("combobox", { name: "Status" }));
  await user.click(screen.getByRole("option", { name: "Failed" }));
  await waitFor(() => expect(requests.some(path => {
    const request = url(path);
    return request.searchParams.get("state") === "Failed" && request.searchParams.get("fromUtc") === "2026-10-01T00:00:00.000Z" &&
      request.searchParams.get("toUtc") === "2026-10-03T00:00:00.000Z" && request.searchParams.get("cursor") === null;
  })).toBe(true));
  expect(screen.getByText("Page 1")).toBeVisible();
  expect(screen.queryByText("Page 2")).not.toBeInTheDocument();
});

test("renders translated controls and localizes execution timestamps", async () => {
  vi.mocked(read).mockResolvedValue(response([execution("execution-1", "NeedsReview")], null));
  render(<JobHistoryTable jobId="job-1" language="pt-BR" timeZone="America/Sao_Paulo" />);
  const table = await screen.findByRole("table", { name: "Histórico de execuções da tarefa" });
  expect(screen.getByRole("button", { name: "Últimos 7 dias" })).toBeVisible();
  expect(within(table).getByText("28/10/2026, 09:00:00")).toBeVisible();
  expect(within(table).getByText("Aguardando revisão")).toBeVisible();
});


test("shows job identity when the history has no results", async () => {
  vi.mocked(read).mockResolvedValue(response([], null));
  render(<JobHistoryTable jobId="job-empty" language="en" timeZone="UTC" />);
  expect(await screen.findByRole("heading", { level: 2, name: "Job history · job-empty" })).toBeVisible();
  expect(await screen.findByText("No executions match this filter.")).toBeVisible();
});
