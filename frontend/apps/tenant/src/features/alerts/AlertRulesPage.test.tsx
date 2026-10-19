import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { AlertRulesPage } from "./AlertRulesPage";

afterEach(() => vi.unstubAllGlobals());

const rule = {
  id: "11111111-1111-4111-8111-111111111111", name: "Repeated failures", condition: "RepeatedFailure", threshold: 3,
  windowSeconds: 300, graceSeconds: 30, reminderSeconds: 600, sinkId: "22222222-2222-4222-8222-222222222222", enabled: true, version: "cm9sZQ==",
};
const sink = {
  id: "22222222-2222-4222-8222-222222222222", name: "Ops webhook", type: "webhook",
  configuration: { url: "https://example.test/alerts", headers: [{ name: "Authorization", secretReference: "vault/ops-token", value: "must-never-render-or-send" }] },
  maxAttempts: 5, retryBaseDelaySeconds: 10, retryMaxDelaySeconds: 120, timeoutSeconds: 15, enabled: true, version: "c2luaw==",
};

test("webhook updates send only secret references with If-Match and never expose header values", async () => {
  let submitted: Record<string, unknown> | undefined;
  let submittedHeaders: HeadersInit | undefined;
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/incidents" || url.pathname === "/api/alerts/deliveries") return new Response("[]");
    if (url.pathname === "/api/alert-rules") return new Response(JSON.stringify([rule]));
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname.endsWith(`/api/alert-rules/${rule.id}`)) return new Response(JSON.stringify(rule), { headers: { ETag: '"rule-etag"' } });
    if (url.pathname.endsWith(`/api/alert-sinks/${sink.id}`) && init?.method === "PUT") {
      const requestBody = JSON.parse(String(init.body)) as Record<string, unknown>;
      submitted = requestBody; submittedHeaders = init.headers;
      return new Response(JSON.stringify({ definition: sink, version: "bmV4dA==" }), { headers: { ETag: '"next"' } });
    }
    if (url.pathname.endsWith(`/api/alert-sinks/${sink.id}`)) return new Response(JSON.stringify(sink), { headers: { ETag: '"sink-etag"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);

  const reference = await screen.findByRole("textbox", { name: "Secret reference" });
  expect(reference).toHaveValue("vault/ops-token");
  expect(screen.queryByDisplayValue("must-never-render-or-send")).not.toBeInTheDocument();
  await user.click(screen.getAllByRole("button", { name: "Save" })[1]);

  await waitFor(() => expect(submitted).toBeDefined());
  const payload = submitted as Record<string, any>;
  expect(payload.configuration).toEqual({ url: "https://example.test/alerts", headers: [{ name: "Authorization", secretReference: "vault/ops-token" }] });
  expect(JSON.stringify(payload)).not.toContain("must-never-render-or-send");
  expect(new Headers(submittedHeaders).get("If-Match")).toBe('"sink-etag"');
});

test("shows stale-version errors for concurrent rule edits", async () => {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname.startsWith("/api/alerts/")) return new Response("[]");
    if (url.pathname === "/api/alert-rules") return new Response(JSON.stringify([rule]));
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-rules/${rule.id}` && init?.method === "PUT") return new Response("{}", { status: 412 });
    if (url.pathname === `/api/alert-rules/${rule.id}`) return new Response(JSON.stringify(rule), { headers: { ETag: '"rule-etag"' } });
    if (url.pathname === `/api/alert-sinks/${sink.id}`) return new Response(JSON.stringify(sink), { headers: { ETag: '"sink-etag"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await screen.findByRole("textbox", { name: "Rule name" });
  await user.click(screen.getAllByRole("button", { name: "Save" })[0]);
  expect(await screen.findByText(translations.en.stale)).toBeVisible();
});

test("read users can view incidents and deliveries without requesting management resources", async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname.endsWith("/incidents")) return new Response(JSON.stringify([{ incidentId: "i-1", ruleName: "Restart failures", condition: "RepeatedFailure", subjectKey: "job/demo", state: "Open", openedAtUtc: "2026-10-19T10:00:00Z", lastObservedAtUtc: "2026-10-19T10:01:00Z", resolvedAtUtc: null, safeCode: null }]));
    if (url.pathname.endsWith("/deliveries")) return new Response(JSON.stringify([{ deliveryId: "d-1", incidentId: "i-1", kind: "Opened", status: "Pending", attemptCount: 1, nextAvailableAtUtc: "2026-10-19T10:02:00Z", completedAtUtc: null, safeCode: null }]));
    return new Response("{}", { status: 403 });
  });
  vi.stubGlobal("fetch", fetchMock);
  render(<AlertRulesPage t={translations.en} canManage={false} />);
  expect(await screen.findByText("Restart failures")).toBeVisible();
  expect(await screen.findByText("Pending")).toBeVisible();
  expect(fetchMock.mock.calls.map(([input]) => String(input)).join(" ")).not.toMatch(/alert-rules|alert-sinks/);
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
});
