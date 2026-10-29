import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { read } from "../../api/client";
import { AuditPage } from "./AuditPage";

vi.mock("../../api/client", () => ({ read: vi.fn() }));
afterEach(() => { vi.clearAllMocks(); vi.useRealTimers(); });

const event = {
  eventId: "audit-1", eventType: "ManualRunAccepted", actorSubject: "actor-demo",
  authenticationMode: "Session", correlationId: "corr-demo", occurredAtUtc: "2026-03-08T12:30:00Z",
};
const requests: URL[] = [];
function asUrl(path: string) { return new URL(path, window.location.origin); }
function response(path: string) {
  const url = asUrl(path);
  requests.push(url);
  if (url.pathname === "/api/audit/event-types") return { data: ["ManualRunAccepted", "JobRevisionSaved"] } as never;
  return { data: { items: [event], nextCursor: url.searchParams.has("cursor") ? null : "cursor-2" } } as never;
}

async function flush() { await act(async () => { await Promise.resolve(); await Promise.resolve(); }); }

test("filters retained audit by exact event type and period, preserves search, and resets paging", async () => {
  requests.length = 0;
  const user = userEvent.setup();
  vi.mocked(read).mockImplementation(async (path: string) => response(path));
  render(<AuditPage language="en" timeZone="America/New_York" />);

  const table = await screen.findByRole("table", { name: "Audit" });
  expect(within(table).getByText("ManualRunAccepted")).toBeVisible();
  expect(await screen.findByRole("combobox", { name: "Event type" })).toBeVisible();
  expect(requests.some(url => url.pathname === "/api/audit/event-types")).toBe(true);
  expect(requests.find(url => url.pathname === "/api/audit")?.searchParams.get("fromUtc")).toBeTruthy();

  await user.type(screen.getByRole("textbox", { name: "Search audit" }), "actor-demo");
  await user.click(screen.getByRole("combobox", { name: "Event type" }));
  await user.click(screen.getByRole("option", { name: "ManualRunAccepted" }));
  await waitFor(() => expect(requests.some(url => url.pathname === "/api/audit" &&
    url.searchParams.get("search") === "actor-demo" && url.searchParams.get("eventType") === "ManualRunAccepted")).toBe(true));

  await user.click(screen.getByRole("button", { name: "Next" }));
  await flush();
  expect(screen.getByText("Page 2")).toBeVisible();
  expect(requests.some(url => url.searchParams.get("cursor") === "cursor-2")).toBe(true);

  await user.click(screen.getByRole("combobox", { name: "Event type" }));
  await user.click(screen.getByRole("option", { name: "JobRevisionSaved" }));
  await waitFor(() => expect(requests.some(url => url.pathname === "/api/audit" &&
    url.searchParams.get("eventType") === "JobRevisionSaved" && url.searchParams.get("search") === "actor-demo" &&
    url.searchParams.get("cursor") === null)).toBe(true));
  expect(screen.getByText("Page 1")).toBeVisible();

  await user.click(screen.getByRole("button", { name: "Custom" }));
  fireEvent.change(screen.getByLabelText("Start date"), { target: { value: "2026-03-08" } });
  fireEvent.change(screen.getByLabelText("End date"), { target: { value: "2026-03-09" } });
  await waitFor(() => expect(requests.some(url => url.pathname === "/api/audit" &&
    url.searchParams.get("fromUtc") === "2026-03-08T05:00:00.000Z" &&
    url.searchParams.get("toUtc") === "2026-03-10T04:00:00.000Z" &&
    url.searchParams.get("eventType") === "JobRevisionSaved" && url.searchParams.get("search") === "actor-demo")).toBe(true));
  expect(requests.filter(url => url.pathname === "/api/audit").every(url => url.searchParams.get("limit") === "50")).toBe(true);
});

test("uses Portuguese labels and the selected timezone", async () => {
  requests.length = 0;
  vi.mocked(read).mockImplementation(async (path: string) => response(path));
  render(<AuditPage language="pt-BR" timeZone="America/Sao_Paulo" />);
  const table = await screen.findByRole("table", { name: "Auditoria" });
  expect(await screen.findByRole("combobox", { name: "Tipo de evento" })).toBeVisible();
  expect(screen.getByRole("button", { name: "Últimos 7 dias" })).toBeVisible();
  expect(within(table).getByText("08/03/2026, 09:30:00")).toBeVisible();
  expect(screen.queryByRole("button", { name: /manage|editar|excluir/i })).not.toBeInTheDocument();
});

test("does not crash when the event-type endpoint returns an unexpected object", async () => {
  vi.mocked(read).mockImplementation(async (path: string) => path === "/api/audit/event-types"
    ? { data: { session: "active" } } as never
    : { data: { session: "active" } } as never);
  render(<AuditPage language="en" timeZone="UTC" />);
  expect(await screen.findByRole("table", { name: "Audit" })).toBeVisible();
  expect(screen.getByRole("combobox", { name: "Event type" })).toBeVisible();
  expect(screen.queryByRole("option", { name: "active" })).not.toBeInTheDocument();
  expect(screen.getByText("No audit events match this filter.")).toBeVisible();
});
