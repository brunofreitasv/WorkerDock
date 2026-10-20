import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";
import { translations, type Language } from "@workerdock/ui";
import { JobWizard, selectDefaultTemplate } from "./JobWizard";
import { confirmsScheduleWrite, sameScheduleInput } from "./scheduleInput";

afterEach(() => vi.unstubAllGlobals());

test("new jobs default to the newest selectable version of the first template", () => {
  const item = (version: string, selectable: boolean) => ({ selectable, descriptor: { id: "workerdock.sql-server", version, displayName: "SQL Server", description: "SQL Server", configurationSchema: { type: "object", properties: {} } } });
  expect(selectDefaultTemplate([item("1.0.0", true), item("1.1.0", true), item("2.0.0", false)])?.descriptor.version).toBe("1.1.0");
});

test.each([
  ["-05:00", "-05:00:00"],
  ["+05:30", "05:30:00"],
  ["-00:00", "00:00:00"],
])("schedule confirmation compares equivalent offsets %s and %s", (draftOffset, persistedOffset) => {
  const base = { kind: "Once" as const, cron: null, zoneId: "UTC", localOnce: "2026-11-01T01:30:00" };
  expect(sameScheduleInput({ ...base, selectedOffset: draftOffset }, { ...base, selectedOffset: persistedOffset })).toBe(true);
});

test("schedule write confirmation accepts a resolved offset only for the submitted local instant", () => {
  const request = { kind: "Once" as const, cron: null, zoneId: "America/New_York", localOnce: "2026-01-15T09:00:00", selectedOffset: null };
  expect(confirmsScheduleWrite(request, { ...request, selectedOffset: "-05:00:00" })).toBe(true);
  expect(confirmsScheduleWrite(request, { ...request, selectedOffset: null })).toBe(false);
  expect(confirmsScheduleWrite(request, { ...request, selectedOffset: "not-an-offset" })).toBe(false);
  expect(confirmsScheduleWrite(request, { ...request, localOnce: "2026-01-15T10:00:00", selectedOffset: "-05:00:00" })).toBe(false);
});

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
}, 15_000);

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

test("a selected persisted schedule can be enabled after review", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  const jobId = "11111111-1111-4111-8111-111111111111";
  let scheduleConfirmed = false;
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Scheduled job", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") { scheduleConfirmed = true; body = { input: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused: false }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Scheduled job");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  expect(scheduleConfirmed).toBe(true);
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
  expect(screen.queryByRole("button", { name: "Pause schedule" })).not.toBeInTheDocument();
});

async function startScheduledDraftWithPutFailure(status: 412 | 503) {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  const jobId = "11111111-1111-4111-8111-111111111111";
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let responseStatus = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { responseStatus = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Scheduled failure", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") { responseStatus = status; body = { code: status === 412 ? "StaleVersion" : "Unavailable" }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") { responseStatus = 404; body = { code: "NotFound" }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    return new Response(JSON.stringify(body), { status: responseStatus, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Scheduled failure");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getAllByRole("alert").some(alert => status === 412 ? /draft is preserved/i.test(alert.textContent ?? "") : /schedule draft has not been confirmed/i.test(alert.textContent ?? ""))).toBe(true));
  return user;
}

test("initial schedule PUT 412 enters explicit re-review and cannot enable unseen schedule state", async () => {
  const user = await startScheduledDraftWithPutFailure(412);
  expect(screen.getByRole("button", { name: "Load current revision" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Load current revision" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "I reviewed this current revision" })).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Restore my draft" }));
  expect(screen.getByText(/Scheduled failure/)).toBeVisible();
  expect(screen.getByRole("button", { name: "Save disabled draft" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "I reviewed this current revision" }));
  expect(screen.getByRole("button", { name: "Save disabled draft" })).toBeEnabled();
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeDisabled();
  expect(screen.queryByText(/Manual job/)).not.toBeInTheDocument();
});

test("restoring a different local schedule after 412 stays blocked until PUT and GET confirm it", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  const jobId = "11111111-1111-4111-8111-111111111111";
  let schedulePutCount = 0;
  let persistedCron = "0 8 * * 1-5";
  const confirmedCrons: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Scheduled draft", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") {
      schedulePutCount++;
      if (schedulePutCount === 1) { status = 412; body = { code: "StaleVersion" }; }
      else { persistedCron = JSON.parse(String(init?.body)).input.cron; status = 202; body = { targetId: jobId }; }
    }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") { confirmedCrons.push(persistedCron); body = { input: { kind: "Cron", cron: persistedCron, zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused: false }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === `/api/jobs/${jobId}` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Scheduled draft");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Load current revision" })).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Load current revision" }));
  await waitFor(() => expect(screen.getByDisplayValue("0 8 * * 1-5")).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Restore my draft" }));
  expect(screen.getByDisplayValue("0 9 * * 1-5")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "I reviewed this current revision" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Back" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  expect(confirmedCrons.at(-1)).toBe("0 9 * * 1-5");
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
});

test("restored one-time schedule confirms semantic UTC offset despite server seconds formatting", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  const jobId = "11111111-1111-4111-8111-111111111111";
  let schedulePutCount = 0;
  let persistedSchedule = { kind: "Once", cron: null, zoneId: "America/New_York", localOnce: "2026-11-01T01:30:00", selectedOffset: "-05:00:00" };
  let confirmedOffset = "";
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["America/New_York", "UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "One-time draft", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") {
      schedulePutCount++;
      if (schedulePutCount === 1) { status = 412; body = { code: "StaleVersion" }; }
      else {
        const submitted = JSON.parse(String(init?.body)).input;
        persistedSchedule = { ...submitted, selectedOffset: submitted.selectedOffset.length === 6 ? `${submitted.selectedOffset}:00` : submitted.selectedOffset };
        confirmedOffset = persistedSchedule.selectedOffset;
        status = 202; body = { targetId: jobId };
      }
    }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") body = { input: persistedSchedule, policy: { mode: "Skip" }, paused: false };
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === `/api/jobs/${jobId}` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "One-time draft");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("combobox", { name: "Schedule kind" }));
  await user.click(await screen.findByRole("option", { name: translations.en.oneTimeSchedule }));
  await user.type(screen.getByLabelText("Local date and time"), "2026-11-01T01:30");
  await user.type(screen.getByLabelText("UTC offset"), "-05:00");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Load current revision" })).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Load current revision" }));
  await waitFor(() => expect(screen.getByDisplayValue("-05:00")).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Restore my draft" }));
  expect(screen.getByDisplayValue("2026-11-01T01:30")).toBeVisible();
  expect(screen.getByDisplayValue("-05:00")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "I reviewed this current revision" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  expect(confirmedOffset).toBe("-05:00:00");
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
});

test("unambiguous one-time schedule with no selected offset confirms the server-resolved offset and can be enabled", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  const jobId = "11111111-1111-4111-8111-111111111111";
  let submittedOffset: string | null | undefined;
  let scheduleConfirmed = false;
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["America/New_York", "UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Winter once", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") { submittedOffset = JSON.parse(String(init?.body)).input.selectedOffset; status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") { scheduleConfirmed = true; body = { input: { kind: "Once", cron: null, zoneId: "America/New_York", localOnce: "2026-01-15T09:00:00", selectedOffset: "-05:00:00" }, policy: { mode: "Skip" }, paused: false }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === `/api/jobs/${jobId}` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Winter once");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("combobox", { name: "Schedule kind" }));
  await user.click(await screen.findByRole("option", { name: translations.en.oneTimeSchedule }));
  await user.type(screen.getByLabelText("Local date and time"), "2026-01-15T09:00");
  await user.click(screen.getByRole("combobox", { name: "Time zone" }));
  await user.click(await screen.findByRole("option", { name: "America/New_York" }));
  expect(screen.getByLabelText("UTC offset")).toHaveValue("");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  expect(submittedOffset).toBeNull();
  expect(scheduleConfirmed).toBe(true);
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
});

test("initial schedule PUT 503 remains unresolved and blocks activation", async () => {
  const user = await startScheduledDraftWithPutFailure(503);
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("button", { name: "Enable job" })).toBeDisabled();
  expect(screen.getAllByRole("alert").some(alert => /schedule draft has not been confirmed/i.test(alert.textContent ?? ""))).toBe(true);
  expect(screen.queryByText(/Manual job/)).not.toBeInTheDocument();
});

test("manual enable is available only after a deleted schedule is confirmed absent", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  let scheduleExists = false;
  let confirmedAbsent = false;
  const jobId = "11111111-1111-4111-8111-111111111111";
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/time-zones") body = ["UTC"];
    else if (url.pathname === "/api/jobs" && method === "POST") { status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/configuration`) body = { revision: { jobId, revision: 1, name: "Manual job", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: false, archived: false, concurrencyLimit: 1 };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "PUT") { scheduleExists = true; status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "DELETE") { scheduleExists = false; status = 202; body = { targetId: jobId }; }
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET" && scheduleExists) body = { input: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused: false };
    else if (url.pathname === `/api/jobs/${jobId}/schedule` && method === "GET") { confirmedAbsent = true; status = 404; body = { code: "NotFound" }; }
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === `/api/jobs/${jobId}` && method === "PUT") { status = 202; body = { targetId: jobId }; }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  }));
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Manual job");
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
  await waitFor(() => expect(screen.getByText("Disabled draft saved.")).toBeVisible());
  expect(confirmedAbsent).toBe(true);
  expect(screen.getByText(/Manual job/)).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByText(/Manual job/)).toBeVisible();
  expect(screen.getByRole("button", { name: "Enable job" })).toBeEnabled();
});
