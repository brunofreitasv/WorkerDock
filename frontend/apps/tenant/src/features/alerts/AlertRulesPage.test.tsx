import { act, render, screen, waitFor } from "@testing-library/react";
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
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [], nextCursor: null })); if (url.pathname === "/api/alerts/incidents" || url.pathname === "/api/alerts/deliveries") return new Response(JSON.stringify({ items: [], nextCursor: null }));
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

test("managers create webhook sinks without If-Match and load the saved ETag", async () => {
  const id = "33333333-3333-4333-8333-333333333333";
  const calls: Array<{ path: string; init?: RequestInit }> = [];
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost"); calls.push({ path: url.pathname, init });
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [], nextCursor: null })); if (url.pathname === "/api/alerts/incidents" || url.pathname === "/api/alerts/deliveries") return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-sinks/${id}` && init?.method === "PUT") return new Response(JSON.stringify({ definition: { ...sink, id }, version: "bmV3" }));
    if (url.pathname === `/api/alert-sinks/${id}`) return new Response(JSON.stringify({ ...sink, id, name: "Created sink" }), { headers: { ETag: '"created-etag"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Created sink");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "https://example.test/hook");
  await user.click(screen.getByRole("button", { name: "Add header" }));
  await user.type(screen.getAllByRole("textbox", { name: "Header name" })[0], "Authorization");
  await user.type(screen.getAllByRole("textbox", { name: "Secret reference" })[0], "vault/created-token");
  await user.click(screen.getAllByRole("button", { name: "Save" }).at(-1)!);

  await waitFor(() => expect(calls.some(call => call.path === `/api/alert-sinks/${id}` && call.init?.method === "PUT")).toBe(true));
  const put = calls.find(call => call.path === `/api/alert-sinks/${id}` && call.init?.method === "PUT")!;
  expect(new Headers(put.init?.headers).has("If-Match")).toBe(false);
  expect(JSON.parse(String(put.init?.body))).toMatchObject({ id, configuration: { url: "https://example.test/hook", headers: [{ name: "Authorization", secretReference: "vault/created-token" }] } });
  await waitFor(() => expect(calls.some(call => call.path === `/api/alert-sinks/${id}` && !call.init?.method)).toBe(true));
  expect(await screen.findByRole("textbox", { name: "Sink name" })).toHaveValue("Created sink");
}, 15000);

test("managers create disabled rules then enable them with the loaded ETag", async () => {
  const id = "44444444-4444-4444-8444-444444444444";
  const calls: Array<{ path: string; init?: RequestInit }> = [];
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost"); calls.push({ path: url.pathname, init });
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-rules/${id}` && init?.method === "PUT") {
      const body = JSON.parse(String(init.body));
      return new Response(JSON.stringify({ rule: body, version: body.enabled ? "enabled-v2" : "created-v1" }));
    }
    if (url.pathname === `/api/alert-rules/${id}`) {
      const enabled = calls.some(call => call.path === `/api/alert-rules/${id}` && call.init?.method === "PUT" && JSON.parse(String(call.init.body)).enabled);
      return new Response(JSON.stringify({ ...rule, id, name: "Created rule", enabled }), { headers: { ETag: enabled ? '"enabled-v2"' : '"created-v1"' } });
    }
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New alert rule" }));
  await user.type(screen.getByRole("textbox", { name: "Rule name" }), "Created rule");
  await user.click(screen.getByRole("button", { name: "Create rule" }));
  await waitFor(() => expect(calls.some(call => call.path === `/api/alert-rules/${id}` && call.init?.method === "PUT")).toBe(true));
  const create = calls.find(call => call.path === `/api/alert-rules/${id}` && call.init?.method === "PUT")!;
  expect(JSON.parse(String(create.init?.body))).toMatchObject({ id, enabled: false, condition: "RepeatedFailure", threshold: 3, windowSeconds: 300, graceSeconds: 0, reminderSeconds: 0, sinkId: sink.id });
  expect(new Headers(create.init?.headers).has("If-Match")).toBe(false);
  await user.click(await screen.findByRole("button", { name: "Enable" }));
  await waitFor(() => expect(calls.filter(call => call.path === `/api/alert-rules/${id}` && call.init?.method === "PUT")).toHaveLength(2));
  const enable = calls.filter(call => call.path === `/api/alert-rules/${id}` && call.init?.method === "PUT")[1];
  expect(JSON.parse(String(enable.init?.body)).enabled).toBe(true);
  expect(new Headers(enable.init?.headers).get("If-Match")).toBe('"created-v1"');
});

test("rule creation without an enabled sink gives guidance and keeps the draft", async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New alert rule" }));
  expect(await screen.findByText(translations.en.alertRuleNeedsSink)).toBeVisible();
  expect(screen.getByRole("button", { name: "Create rule" })).toBeDisabled();
  expect(fetchMock.mock.calls.some(([input, init]) => String(input).includes("/api/alert-rules/") && init?.method === "PUT")).toBe(false);
});

test("invalid condition threshold and timing fields retain the rule draft", async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New alert rule" }));
  await user.type(screen.getByRole("textbox", { name: "Rule name" }), "Draft rule");
  await user.click(screen.getByRole("combobox", { name: "Condition" }));
  await user.click(await screen.findByRole("option", { name: "Long running" }));
  const threshold = screen.getByRole("spinbutton", { name: "Threshold" });
  await user.clear(threshold); await user.type(threshold, "3");
  const window = screen.getByRole("spinbutton", { name: "Window (seconds)" });
  await user.clear(window); await user.type(window, "2592001");
  const grace = screen.getByRole("spinbutton", { name: "Grace period (seconds)" });
  await user.clear(grace); await user.type(grace, "2592001");
  const reminder = screen.getByRole("spinbutton", { name: "Reminder interval (seconds)" });
  await user.clear(reminder); await user.type(reminder, "2592001");
  await user.click(screen.getByRole("button", { name: "Create rule" }));
  expect(threshold).toHaveValue(3);
  expect(window).toHaveValue(2592001);
  expect(grace).toHaveValue(2592001);
  expect(reminder).toHaveValue(2592001);
  expect(await screen.findByText(translations.en.alertRuleThresholdInvalid)).toBeVisible();
  expect(screen.getByText(translations.en.alertRuleWindowInvalid)).toBeVisible();
  expect(screen.getByText(translations.en.alertRuleGraceInvalid)).toBeVisible();
  expect(screen.getByText(translations.en.alertRuleReminderInvalid)).toBeVisible();
  expect(fetchMock.mock.calls.some(([input, init]) => String(input).includes("/api/alert-rules/") && init?.method === "PUT")).toBe(false);
});

test("invalid webhook fields keep a new sink draft without sending it", async () => {
  const id = "33333333-3333-4333-8333-333333333333";
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", fetchMock);
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Draft sink");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "not a URL");
  await user.click(screen.getByRole("button", { name: "Add header" }));
  await user.type(screen.getAllByRole("textbox", { name: "Header name" })[0], "Bad Header");
  await user.click(screen.getAllByRole("button", { name: "Save" }).at(-1)!);
  expect(screen.getByRole("textbox", { name: "Sink name" })).toHaveValue("Draft sink");
  expect(screen.getByRole("textbox", { name: "Webhook URL" })).toHaveValue("not a URL");
  expect(fetchMock.mock.calls.some(([input, init]) => String(input).includes(`/api/alert-sinks/${id}`) && init?.method === "PUT")).toBe(false);
});

test("a stale sink creation recovers the saved item after a 412", async () => {
  const id = "33333333-3333-4333-8333-333333333333";
  const calls: Array<{ path: string; init?: RequestInit }> = [];
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost"); calls.push({ path: url.pathname, init });
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    if (url.pathname === `/api/alert-sinks/${id}` && init?.method === "PUT") return new Response("{}", { status: 412 });
    if (url.pathname === `/api/alert-sinks/${id}`) return new Response(JSON.stringify({ ...sink, id, name: "Server sink" }), { headers: { ETag: '"server-etag"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Local draft");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "https://example.test/hook");
  const save = () => screen.getAllByRole("button", { name: "Save" }).at(-1)!;
  await user.click(save());
  await waitFor(() => expect(screen.getByRole("textbox", { name: "Sink name" })).toHaveValue("Server sink"));
  expect(calls.filter(call => call.path === `/api/alert-sinks/${id}` && call.init?.method === "PUT")).toHaveLength(1);
  expect(calls.some(call => call.path === `/api/alert-sinks/${id}` && !call.init?.method)).toBe(true);
});

test("non-managers cannot create sinks or request management resources", async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [], nextCursor: null })); if (url.pathname === "/api/alerts/incidents" || url.pathname === "/api/alerts/deliveries") return new Response(JSON.stringify({ items: [], nextCursor: null }));
    return new Response("{}", { status: 403 });
  });
  vi.stubGlobal("fetch", fetchMock);
  render(<AlertRulesPage t={translations.en} canManage={false} />);
  expect(await screen.findByText(translations.en.alertNoIncidents)).toBeVisible();
  expect(screen.queryByRole("button", { name: "New webhook sink" })).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.map(([input]) => String(input)).join(" ")).not.toMatch(/alert-rules|alert-sinks/);
});

test("shows stale-version errors for concurrent rule edits", async () => {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
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
    if (url.pathname.endsWith("/rule-options")) return new Response(JSON.stringify({ items: [{ ruleId: "r-1", name: "Restart failures" }], nextCursor: null }));
    if (url.pathname.endsWith("/incidents")) return new Response(JSON.stringify({ items: [{ incidentId: "i-1", ruleId: "r-1", ruleName: "Restart failures", condition: "RepeatedFailure", subjectKey: "job/demo", state: "Open", openedAtUtc: "2026-10-19T10:00:00Z", lastObservedAtUtc: "2026-10-19T10:01:00Z", resolvedAtUtc: null, safeCode: null }], nextCursor: null }));
    if (url.pathname.endsWith("/deliveries")) return new Response(JSON.stringify({ items: [{ deliveryId: "d-1", incidentId: "i-1", ruleName: "Restart failures", subjectKey: "job/demo", kind: "Open", status: "Pending", attemptCount: 1, nextAvailableAtUtc: "2026-10-19T10:02:00Z", completedAtUtc: null, safeCode: null }], nextCursor: null }));
    return new Response("{}", { status: 403 });
  });
  vi.stubGlobal("fetch", fetchMock);
  render(<AlertRulesPage t={translations.en} canManage={false} />);
  expect(await screen.findByText("Restart failures")).toBeVisible();
  expect(await screen.findByText("Pending")).toBeVisible();
  expect(fetchMock.mock.calls.map(([input]) => String(input)).join(" ")).not.toMatch(/alert-rules|alert-sinks/);
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
});

test("canceling a selection change preserves the current draft and confirming switches items", async () => {
  const secondRule = { ...rule, id: "55555555-5555-4555-8555-555555555555", name: "Second rule" };
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response(JSON.stringify([rule, secondRule]));
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-rules/${rule.id}`) return new Response(JSON.stringify(rule), { headers: { ETag: '"first"' } });
    if (url.pathname === `/api/alert-rules/${secondRule.id}`) return new Response(JSON.stringify(secondRule), { headers: { ETag: '"second"' } });
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  const name = await screen.findByRole("textbox", { name: "Rule name" });
  await user.clear(name); await user.type(name, "Unsaved rule draft");
  await user.click(screen.getByRole("combobox", { name: "Rule" }));
  await user.click(await screen.findByRole("option", { name: /Second rule/ }));
  await user.click(await screen.findByRole("button", { name: "Cancel" }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(screen.getByRole("textbox", { name: "Rule name" })).toHaveValue("Unsaved rule draft");
  await user.click(screen.getByRole("combobox", { name: "Rule" }));
  await user.click(await screen.findByRole("option", { name: /Second rule/ }));
  await user.click(await screen.findByRole("button", { name: "Discard changes" }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(await screen.findByRole("textbox", { name: "Rule name" })).toHaveValue("Second rule");
});

test("a 412 during sink create recovers the same UUID", async () => {
  const id = "66666666-6666-4666-8666-666666666666";
  const calls: string[] = [];
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost"); calls.push(`${init?.method ?? "GET"} ${url.pathname}`);
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    if (url.pathname === `/api/alert-sinks/${id}` && init?.method === "PUT") return new Response("{}", { status: 412 });
    if (url.pathname === `/api/alert-sinks/${id}`) return new Response(JSON.stringify({ ...sink, id, name: "Recovered sink" }), { headers: { ETag: '"recovered"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Local draft");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "https://example.test/hook");
  const save = () => screen.getAllByRole("button", { name: "Save" }).at(-1)!;
  await user.click(save());
  expect(await screen.findByRole("textbox", { name: "Sink name" })).toHaveValue("Recovered sink");
  expect(calls).toContain(`GET /api/alert-sinks/${id}`);
}, 15000);

test("a failed 412 recovery GET preserves and announces the draft", async () => {
  const id = "77777777-7777-4777-8777-777777777777";
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    if (url.pathname === `/api/alert-sinks/${id}` && init?.method === "PUT") return new Response("{}", { status: 412 });
    if (url.pathname === `/api/alert-sinks/${id}`) return new Response("{}", { status: 503 });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Keep after failed recovery");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "https://example.test/hook");
  await user.click(screen.getAllByRole("button", { name: "Save" }).at(-1)!);
  expect(await screen.findByRole("alert")).toHaveTextContent(translations.en.alertSinkCreateError);
  expect(screen.getByRole("textbox", { name: "Sink name" })).toHaveValue("Keep after failed recovery");
}, 15000);

test("late reads for a superseded selection cannot replace the current draft", async () => {
  let releaseOld!: (response: Response) => void;
  const oldResponse = new Promise<Response>(resolve => { releaseOld = resolve; });
  let oldReadCount = 0;
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response(JSON.stringify([rule, { ...rule, id: "55555555-5555-4555-8555-555555555555", name: "Second rule" }]));
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-rules/${rule.id}`) {
      oldReadCount++;
      return oldReadCount === 1 ? oldResponse.then(response => response) : new Response(JSON.stringify(rule), { headers: { ETag: '"first"' } });
    }
    if (url.pathname === "/api/alert-rules/55555555-5555-4555-8555-555555555555") return new Response(JSON.stringify({ ...rule, id: "55555555-5555-4555-8555-555555555555", name: "Second rule" }), { headers: { ETag: '"second"' } });
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  render(<AlertRulesPage t={translations.en} canManage />);
  await screen.findByRole("combobox", { name: "Rule" });
  await user.click(screen.getByRole("combobox", { name: "Rule" }));
  await user.click(await screen.findByRole("option", { name: /Second rule/ }));
  expect(await screen.findByRole("textbox", { name: "Rule name" })).toHaveValue("Second rule");
  releaseOld(new Response(JSON.stringify(rule), { headers: { ETag: '"first"' } }));
  await waitFor(() => expect(screen.getByRole("textbox", { name: "Rule name" })).toHaveValue("Second rule"));
});

test("losing manager permission clears management UI and does not fetch management data again", async () => {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response(JSON.stringify([rule]));
    if (url.pathname === "/api/alert-sinks") return new Response(JSON.stringify([sink]));
    if (url.pathname === `/api/alert-rules/${rule.id}`) return new Response(JSON.stringify(rule), { headers: { ETag: '"rule"' } });
    if (url.pathname === `/api/alert-sinks/${sink.id}`) return new Response(JSON.stringify(sink), { headers: { ETag: '"sink"' } });
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  const view = render(<AlertRulesPage t={translations.en} canManage />);
  await screen.findByRole("textbox", { name: "Rule name" });
  const managementCalls = () => fetchMock.mock.calls.filter(([input]) => /alert-rules|alert-sinks/.test(String(input))).length;
  const before = managementCalls();
  view.rerender(<AlertRulesPage t={translations.en} canManage={false} />);
  expect(await screen.findByText(translations.en.alertNoIncidents)).toBeVisible();
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  expect(managementCalls()).toBe(before);
});

test("a sink recovery response after manager permission is lost cannot repopulate management state", async () => {
  const id = "88888888-8888-4888-8888-888888888888";
  let releasePut!: (response: Response) => void;
  const pendingPut = new Promise<Response>(resolve => { releasePut = resolve; });
  let putCount = 0;
  let sinkListCount = 0;
  let releaseSecondSinkList!: (response: Response) => void;
  const pendingSecondSinkList = new Promise<Response>(resolve => { releaseSecondSinkList = resolve; });
  vi.stubGlobal("crypto", { randomUUID: () => id });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return new Response("[]");
    if (url.pathname === "/api/alert-sinks") {
      sinkListCount++;
      return sinkListCount === 1 ? new Response("[]") : pendingSecondSinkList;
    }
    if (url.pathname === `/api/alert-sinks/${id}` && init?.method === "PUT") { putCount++; return pendingPut; }
    if (url.pathname === `/api/alert-sinks/${id}`) return new Response(JSON.stringify({ ...sink, id, name: "Stale recovered sink" }), { headers: { ETag: '"recovered"' } });
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "csrf" }));
    return new Response("{}", { status: 404 });
  }));
  const user = userEvent.setup();
  const view = render(<AlertRulesPage t={translations.en} canManage />);
  await user.click(await screen.findByRole("button", { name: "New webhook sink" }));
  await user.type(screen.getByRole("textbox", { name: "Sink name" }), "Local draft");
  await user.type(screen.getByRole("textbox", { name: "Webhook URL" }), "https://example.test/hook");
  await user.click(screen.getAllByRole("button", { name: "Save" }).at(-1)!);
  await waitFor(() => expect(putCount).toBe(1));

  view.rerender(<AlertRulesPage t={translations.en} canManage={false} />);
  releasePut(new Response("{}", { status: 412 }));
  await waitFor(() => expect(sinkListCount).toBe(1));
  await new Promise(resolve => setTimeout(resolve, 0));
  view.rerender(<AlertRulesPage t={translations.en} canManage />);
  await waitFor(() => expect(sinkListCount).toBe(2));
  expect(screen.getByRole("combobox", { name: "Webhook sink" })).toHaveAttribute("aria-disabled", "true");
  releaseSecondSinkList(new Response("[]"));
}, 15000);

test("a failed management list request after permission loss does not show a stale error", async () => {
  let rejectRules!: (error: Error) => void;
  const pendingRules = new Promise<Response>((_, reject) => { rejectRules = reject; });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    if (url.pathname === "/api/alerts/rule-options") return new Response(JSON.stringify({ items: [{ ruleId: "22222222-2222-4222-8222-222222222222", name: "Repeated failures" }], nextCursor: null })); if (url.pathname.startsWith("/api/alerts/")) return new Response(JSON.stringify({ items: [], nextCursor: null }));
    if (url.pathname === "/api/alert-rules") return pendingRules;
    if (url.pathname === "/api/alert-sinks") return new Response("[]");
    return new Response("{}", { status: 404 });
  }));
  const view = render(<AlertRulesPage t={translations.en} canManage />);
  await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringContaining("/api/alert-rules"), expect.anything()));
  view.rerender(<AlertRulesPage t={translations.en} canManage={false} />);

  await act(async () => {
    rejectRules(new Error("management request failed"));
    await Promise.resolve();
    await Promise.resolve();
  });

  expect(screen.queryByText(translations.en.requestError)).not.toBeInTheDocument();
}, 15000);
