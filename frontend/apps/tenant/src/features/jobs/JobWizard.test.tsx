import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";
import { translations, type Language } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";

afterEach(() => vi.unstubAllGlobals());

test("changing wizard language preserves the draft and selected local instant, offset, and preview UTC", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input);
    const body = path.endsWith("/api/templates") ? { items: [{ selectable: true, descriptor }] }
      : path.endsWith("/api/time-zones") ? ["America/New_York", "UTC"]
        : path.endsWith("/api/schedules/preview") ? [{ utc: "2026-11-01T06:30:00Z", offset: "-05:00" }]
          : path.endsWith("/api/antiforgery") ? { requestToken: "test" } : {};
    return { ok: true, status: 200, headers: new Headers(), json: async () => body };
  }));
  function Harness() {
    const [language, setLanguage] = useState<Language>("en");
    return <JobWizard t={translations[language]} language={language} setLanguage={setLanguage} />;
  }
  const user = userEvent.setup();
  render(<Harness />);
  await user.type(await screen.findByRole("textbox"), "Draft intact");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("tab", { name: "Schedule" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("combobox", { name: "Schedule kind" }));
  await user.click(await screen.findByRole("option", { name: "One time" }));
  await user.click(screen.getByRole("combobox", { name: "Time zone" }));
  await user.click(await screen.findByRole("option", { name: "America/New_York" }));
  await user.type(screen.getByLabelText("Local date and time"), "2026-11-01T01:30");
  await user.type(screen.getByLabelText("UTC offset"), "-05:00");
  await user.click(screen.getByRole("button", { name: "Preview next runs" }));
  await waitFor(() => expect(screen.getByTestId("resolved-utc")).toHaveValue("2026-11-01T06:30:00Z"));

  await user.click(screen.getByRole("combobox", { name: "Wizard language" }));
  await user.click(await screen.findByRole("option", { name: "Português (Brasil)" }));

  expect(screen.getByDisplayValue("2026-11-01T01:30")).toBeVisible();
  expect(screen.getByDisplayValue("-05:00")).toBeVisible();
  expect(screen.getByTestId("resolved-utc")).toHaveValue("2026-11-01T06:30:00Z");
  await user.click(screen.getByRole("tab", { name: translations["pt-BR"].stageTemplate }));
  expect(screen.getByDisplayValue("Draft intact")).toBeVisible();
});

test("an unconfirmed schedule removal stays unresolved, keeps safety actions, and blocks manual enable", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  let scheduleExists = false;
  let deleted = false;
  const jobId = "11111111-1111-4111-8111-111111111111";
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Demo", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") { scheduleExists = true; status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "DELETE") { scheduleExists = false; deleted = true; status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET" && deleted) { status = 503; body = { code: "Unavailable" }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET" && scheduleExists) body = { input: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused: false };
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === `/api/jobs/${jobId}` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Uncertain removal");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  await user.click(screen.getByRole("tab", { name: "Schedule" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getAllByRole("alert").some(alert => /Schedule removal could not be confirmed/i.test(alert.textContent ?? ""))).toBe(true));
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getAllByRole("alert").some(alert => /Schedule removal could not be confirmed/i.test(alert.textContent ?? ""))).toBe(true);
  expect(screen.getByRole("button", { name: "Pause schedule" })).toBeEnabled();
  expect(screen.getByRole("button", { name: "Disable" })).toBeEnabled();
  expect(screen.getByRole("button", { name: "Enable job" })).toBeDisabled();
  expect(screen.queryByText("Manual job")).not.toBeInTheDocument();
});

test("duplicating a scheduled job uses the target 404 schedule state and enables manual activation", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  let scheduleExists = false;
  const sourceId = "11111111-1111-4111-8111-111111111111";
  const targetId = "22222222-2222-4222-8222-222222222222";
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    const id = url.pathname.split("/").at(-2);
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: sourceId }; }
    else if (url.pathname.endsWith("/configuration")) {
      const jobId = id ?? sourceId;
      body = { revision: { jobId, revision: 1, name: jobId === targetId ? "Demo copy" : "Demo", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    } else if (url.pathname === `/api/jobs/${sourceId}/schedule` && method === "PUT") { scheduleExists = true; status = 202; body = { targetId: sourceId }; }
    else if (url.pathname.endsWith("/schedule") && method === "GET" && id === sourceId && scheduleExists) body = { input: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused: false };
    else if (url.pathname.endsWith("/schedule") && method === "GET") { status = 404; body = { code: "NotFound" }; }
    else if (url.pathname.endsWith("/actions") && method === "POST") { status = 202; body = { targetId: JSON.parse(String(init?.body)).targetJobId ?? targetId }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Demo");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Duplicate" }));
  await waitFor(() => expect(screen.getByText(/Manual job/)).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
});
