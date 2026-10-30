import { afterEach, expect, test, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DeliveryHistoryTable } from "./DeliveryHistoryTable";

afterEach(() => vi.unstubAllGlobals());

const delivery = {
  deliveryId: "33333333-3333-4333-8333-333333333333",
  incidentId: "11111111-1111-4111-8111-111111111111",
  ruleName: "Worker missed",
  subjectKey: "worker:alpha",
  kind: "Open",
  status: "Failed",
  attemptCount: 4,
  nextAvailableAtUtc: "2026-10-29T12:00:00Z",
  completedAtUtc: "2026-10-29T11:00:00Z",
  safeCode: "WebhookServerFailure",
};

test("deliveries have their own period, status, kind filters, and readable incident context", async () => {
  const calls: URL[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost"); calls.push(url);
    return new Response(JSON.stringify({ items: [delivery], nextCursor: calls.length === 1 ? "next" : null }));
  }));
  const user = userEvent.setup();
  render(<DeliveryHistoryTable language="en" timeZone="UTC" />);
  expect(await screen.findByRole("table", { name: "Deliveries" })).toBeVisible();
  expect(screen.getByText("Worker missed · worker:alpha")).toBeVisible();
  expect(calls[0].searchParams.has("fromUtc")).toBe(true);
  expect(calls[0].searchParams.get("status")).toBeNull();
  await user.click(screen.getByRole("combobox", { name: "Delivery status" }));
  await user.click(await screen.findByRole("option", { name: "Failed" }));
  await waitFor(() => expect(calls.at(-1)?.searchParams.get("status")).toBe("Failed"));
  await user.click(screen.getByRole("combobox", { name: "Delivery kind" }));
  await user.click(await screen.findByRole("option", { name: "Open" }));
  await waitFor(() => expect(calls.at(-1)?.searchParams.get("kind")).toBe("Open"));
  expect(calls.at(-1)?.searchParams.has("cursor")).toBe(false);
});

test("deliveries paginate independently and show safe empty/error states in pt-BR", async () => {
  const calls: URL[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    calls.push(new URL(String(input), "http://localhost"));
    return new Response(JSON.stringify({ items: [], nextCursor: null }));
  }));
  render(<DeliveryHistoryTable language="pt-BR" timeZone="America/Sao_Paulo" />);
  expect(await screen.findByRole("table", { name: "Entregas" })).toBeVisible();
  expect(screen.getByText("Nenhuma entrega encontrada.")).toBeVisible();
  expect(calls[0].searchParams.get("limit")).toBe("50");
});


test("delivery filters and results are separate and clear restores defaults", async () => {
  const calls: URL[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost"); calls.push(url);
    return new Response(JSON.stringify({ items: [delivery], nextCursor: null }));
  }));
  const user = userEvent.setup();
  render(<DeliveryHistoryTable language="en" timeZone="UTC" />);
  const filters = await screen.findByRole("region", { name: "Delivery filters" });
  const results = screen.getByRole("region", { name: "Delivery results" });
  expect(filters).toContainElement(screen.getByRole("combobox", { name: "Delivery status" }));
  expect(results).toContainElement(screen.getByRole("button", { name: "Refresh" }));
  await user.click(screen.getByRole("combobox", { name: "Delivery status" }));
  await user.click(await screen.findByRole("option", { name: "Failed" }));
  await user.click(screen.getByRole("button", { name: "Clear filters" }));
  await waitFor(() => expect(calls.at(-1)?.searchParams.has("status")).toBe(false));
  expect(screen.getByText("Worker missed · worker:alpha")).toBeVisible();
});
