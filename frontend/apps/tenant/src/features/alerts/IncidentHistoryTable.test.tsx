import { afterEach, expect, test, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { IncidentHistoryTable } from "./IncidentHistoryTable";

afterEach(() => vi.unstubAllGlobals());

const openIncident = {
  incidentId: "11111111-1111-4111-8111-111111111111",
  ruleId: "22222222-2222-4222-8222-222222222222",
  ruleName: "Worker missed",
  condition: "WorkerUnavailable",
  subjectKey: "worker:alpha",
  state: "Open",
  openedAtUtc: "2026-04-01T00:00:00Z",
  lastObservedAtUtc: "2026-04-19T10:00:00Z",
  resolvedAtUtc: null,
  safeCode: "WorkerUnavailable",
};

function stubPages(incident = openIncident) {
  const calls: URL[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    calls.push(url);
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: openIncident.ruleId, name: "Worker missed" }], nextCursor: null }));
    const incidentCount = calls.filter(call => call.pathname === "/api/alerts/incidents").length;
    return new Response(JSON.stringify({ items: [incident], nextCursor: incidentCount === 1 ? "next" : null }));
  }));
  return calls;
}

test("active incidents include old open items and do not apply a date bound by default", async () => {
  const calls = stubPages();
  render(<IncidentHistoryTable language="en" timeZone="UTC" />);
  expect(await screen.findByRole("table", { name: "Incidents" })).toBeVisible();
  expect(screen.getByText("Worker missed")).toBeVisible();
  const initial = calls.find(call => call.pathname === "/api/alerts/incidents")!;
  expect(initial.searchParams.get("state")).toBe("Active");
  expect(initial.searchParams.has("fromUtc")).toBe(false);
  expect(initial.searchParams.has("toUtc")).toBe(false);
});

test("incident state and rule filters restart paging and query the server", async () => {
  const calls = stubPages();
  const user = userEvent.setup();
  render(<IncidentHistoryTable language="en" timeZone="UTC" />);
  await screen.findByRole("table", { name: "Incidents" });
  await user.click(screen.getByRole("button", { name: "Next" }));
  await waitFor(() => expect(calls.filter(call => call.pathname === "/api/alerts/incidents").length).toBe(2));
  await user.click(screen.getByRole("combobox", { name: "Incident state" }));
  await user.click(await screen.findByRole("option", { name: "Resolved" }));
  await waitFor(() => expect(calls.at(-1)?.searchParams.get("state")).toBe("Resolved"));
  expect(calls.at(-1)?.searchParams.has("cursor")).toBe(false);
  expect(calls.at(-1)?.searchParams.has("fromUtc")).toBe(true);
  await user.click(screen.getByRole("combobox", { name: "Incident rule" }));
  await user.click(await screen.findByRole("option", { name: "Worker missed" }));
  await waitFor(() => expect(calls.at(-1)?.searchParams.get("ruleId")).toBe(openIncident.ruleId));
});

test("incident dates and pages render in the selected timezone with safe empty state", async () => {
  const calls = stubPages({ ...openIncident, openedAtUtc: "2026-10-29T10:00:00Z", lastObservedAtUtc: "2026-10-29T10:00:00Z" });
  render(<IncidentHistoryTable language="pt-BR" timeZone="America/Sao_Paulo" />);
  expect(await screen.findByRole("table", { name: "Incidentes" })).toBeVisible();
  expect(screen.getAllByText(/29 de out\. de 2026/)).toHaveLength(2);
  expect(calls.find(call => call.pathname === "/api/alerts/incidents")?.searchParams.get("limit")).toBe("50");
});
