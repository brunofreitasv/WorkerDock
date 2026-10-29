import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { ExecutionHistoryPage } from "./ExecutionHistoryPage";

vi.mock("../../api/client", () => ({ read: vi.fn() }));

afterEach(() => { vi.clearAllMocks(); vi.useRealTimers(); });

const execution = {
  executionId: "execution-1", jobId: "job-1", jobName: "Nightly report", templateId: "http",
  state: "Failed", triggerKind: "Schedule", createdAtUtc: "2026-10-20T12:00:00Z",
  startedAtUtc: "2026-10-20T12:00:05Z", duration: "00:00:42",
};

function page(nextCursor: string | null = "cursor-2") {
  return { data: { items: [execution], nextCursor } } as never;
}

function requestUrl(path: string) {
  return new URL(path, window.location.origin);
}

async function flushPolling() {
  await act(async () => { await Promise.resolve(); await Promise.resolve(); });
}

test("filters history, opens an execution, pages, and resets the cursor when filters change", async () => {
  const user = userEvent.setup();
  const onExecution = vi.fn();
  const requests: string[] = [];
  vi.mocked(read).mockImplementation(async (path: string) => {
    requests.push(path);
    return page();
  });

  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={onExecution} />);

  const table = await screen.findByRole("table", { name: "Execution history" });
  expect(within(table).getByText("Nightly report")).toBeVisible();
  expect(within(table).getByText("http")).toBeVisible();
  expect(within(table).getByText("00:00:42")).toBeVisible();
  await user.click(within(table).getByRole("button", { name: "Open Nightly report" }));
  expect(onExecution).toHaveBeenCalledWith("execution-1");

  const initial = requestUrl(requests[0]);
  expect(initial.pathname).toBe("/api/monitoring/executions");
  expect(initial.searchParams.get("fromUtc")).toBeTruthy();
  expect(initial.searchParams.get("toUtc")).toBeTruthy();
  expect(initial.searchParams.get("limit")).toBe("50");

  await user.type(screen.getByRole("textbox", { name: "Search executions" }), "Nightly");
  fireEvent.change(screen.getByRole("textbox", { name: "Template" }), { target: { value: "http" } });
  await user.click(screen.getByRole("combobox", { name: "Status" }));
  await user.click(screen.getByRole("option", { name: "Failed" }));
  await user.click(screen.getByRole("button", { name: "Custom" }));
  fireEvent.change(screen.getByLabelText("Start date"), { target: { value: "2026-10-01" } });
  fireEvent.change(screen.getByLabelText("End date"), { target: { value: "2026-10-02" } });

  await waitFor(() => expect(requests.some(path => {
    const url = requestUrl(path);
    return url.searchParams.get("search") === "Nightly" && url.searchParams.get("templateId") === "http" &&
      url.searchParams.get("state") === "Failed" && url.searchParams.get("fromUtc") === "2026-10-01T00:00:00.000Z" &&
      url.searchParams.get("toUtc") === "2026-10-03T00:00:00.000Z";
  })).toBe(true));

  await user.click(screen.getByRole("button", { name: "Next" }));
  await waitFor(() => expect(screen.getByText("Page 2")).toBeVisible());
  await waitFor(() => expect(requests.some(path => requestUrl(path).searchParams.get("cursor") === "cursor-2")).toBe(true));
  await user.click(screen.getByRole("button", { name: "Previous" }));
  await waitFor(() => expect(screen.getByText("Page 1")).toBeVisible());

  await user.clear(screen.getByRole("textbox", { name: "Template" }));
  await waitFor(() => expect(requests.some(path => {
    const url = requestUrl(path);
    return url.searchParams.get("state") === "Failed" && url.searchParams.get("templateId") === null && url.searchParams.get("cursor") === null;
  })).toBe(true));
  expect(screen.queryByRole("button", { name: /enable|retry|manage/i })).not.toBeInTheDocument();
});

test("shows a useful empty state when no executions match", async () => {
  vi.mocked(read).mockResolvedValue({ data: { items: [], nextCursor: null } } as never);
  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={vi.fn()} />);

  expect(await screen.findByText("No executions match this filter.")).toBeVisible();
});

test("shows unavailable and stale messages without discarding the last page", async () => {
  const user = userEvent.setup();
  vi.mocked(read).mockResolvedValueOnce(page(null)).mockRejectedValueOnce(new Error("offline"));
  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={vi.fn()} />);

  expect(await screen.findByText("Nightly report")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Refresh" }));
  expect(await screen.findByText(/Showing stale results\./)).toBeVisible();
  expect(screen.getByText("Nightly report")).toBeVisible();
});

test("Refresh and Retry restart at page one with a fresh preset range anchor", async () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-29T12:00:00Z"));
  const requests: string[] = [];
  vi.mocked(read).mockImplementation(async (path: string) => {
    requests.push(path);
    if (requests.length === 3) throw new Error("offline");
    const hasCursor = requestUrl(path).searchParams.has("cursor");
    return { data: { items: [execution], nextCursor: hasCursor ? null : "cursor-2" } } as never;
  });

  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={vi.fn()} />);
  await flushPolling();
  fireEvent.click(screen.getByRole("button", { name: "Next" }));
  await flushPolling();
  expect(screen.getByText("Page 2")).toBeVisible();

  vi.setSystemTime(new Date("2026-10-30T12:00:00Z"));
  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  await flushPolling();
  const refreshRequest = requestUrl(requests[2]);
  expect(refreshRequest.searchParams.get("cursor")).toBeNull();
  expect(refreshRequest.searchParams.get("toUtc")).toBe("2026-10-30T12:00:00.000Z");
  expect(screen.getByText("Page 1")).toBeVisible();
  expect(screen.getByRole("button", { name: "Retry" })).toBeVisible();

  vi.setSystemTime(new Date("2026-10-31T12:00:00Z"));
  fireEvent.click(screen.getByRole("button", { name: "Retry" }));
  await flushPolling();
  const retryRequest = requestUrl(requests[3]);
  expect(retryRequest.searchParams.get("cursor")).toBeNull();
  expect(retryRequest.searchParams.get("toUtc")).toBe("2026-10-31T12:00:00.000Z");
  expect(screen.getByText("Page 1")).toBeVisible();
});

test("shows an unavailable message when the first history request fails", async () => {
  vi.mocked(read).mockRejectedValue(new Error("offline"));
  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={vi.fn()} />);

  expect(await screen.findByText("Execution history is unavailable.")).toBeVisible();
});

test("uses Portuguese labels and the selected timezone", async () => {
  vi.mocked(read).mockResolvedValue(page(null));
  render(<ExecutionHistoryPage language="pt-BR" timeZone="America/Sao_Paulo" onExecution={vi.fn()} />);

  const table = await screen.findByRole("table", { name: "Histórico de execuções" });
  expect(screen.getByRole("button", { name: "Últimos 7 dias" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Próxima" })).toBeDisabled();
  expect(within(table).getByText("20/10/2026, 09:00:00")).toBeVisible();
});

test("keeps days and time in durations over 24 hours", async () => {
  vi.mocked(read).mockResolvedValue({ data: { items: [{ ...execution, duration: "1.02:03:04" }], nextCursor: null } } as never);
  render(<ExecutionHistoryPage language="en" timeZone="UTC" onExecution={vi.fn()} />);
  const table = await screen.findByRole("table", { name: "Execution history" });
  expect(within(table).getByText("1d 02:03:04")).toBeVisible();
});
