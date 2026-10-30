import { expect, test } from "@playwright/test";

const template = { id: "sample", version: "1", displayName: "Sample", description: "Safe sample", configurationSchema: { type: "object", additionalProperties: false, properties: {} } };
const jobId = "11111111-1111-4111-8111-111111111111";

async function setup(page: import("@playwright/test").Page, options: { initialSchedule?: boolean; deleteFails?: boolean; scheduleReadFailsAfterDelete?: boolean; rejectScheduleOnce?: boolean } = {}) {
  let enabled = false;
  let paused = false;
  let rejectEditedNameOnce = true;
  let rejectEnableOnce = false;
  let rejectScheduleOnce = options.rejectScheduleOnce ?? true;
  let scheduleExists = options.initialSchedule ?? false;
  let deleteConfirmed = false;
  let etag = '"initial"';
  const duplicates = new Set<string>();
  const requests: { method: string; path: string; body?: any; etag?: string }[] = [];
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "test-csrf" } }));
  await page.route("**/api/templates", route => route.fulfill({ json: { items: [{ selectable: true, descriptor: template }] } }));
  await page.route("**/api/time-zones", route => route.fulfill({ json: ["America/New_York", "UTC"] }));
  await page.route("**/api/jobs", route => { requests.push({ method: "POST", path: "/api/jobs" }); return route.fulfill({ status: 202, json: { targetId: jobId, commandId: "draft-command" } }); });
  await page.route("**/api/jobs/*/configuration", route => { const id = new URL(route.request().url()).pathname.split("/").at(-2) ?? jobId; return route.fulfill({ json: { revision: { jobId: id, revision: 1, name: "Demo", templateId: "sample", templateVersion: "1", configuration: {}, profileId: null, policy: { timeout: null } }, version: [1], enabled: duplicates.has(id) ? false : enabled, archived: false, concurrencyLimit: 1 }, headers: { ETag: etag } }); });
  await page.route("**/api/jobs/*/schedule", async route => {
    const scheduleJobId = new URL(route.request().url()).pathname.split("/").at(-2);
    requests.push({ method: route.request().method(), path: route.request().url(), body: route.request().postDataJSON(), etag: route.request().headers()["if-match"] });
    if (route.request().method() === "GET" && scheduleJobId !== jobId) return route.fulfill({ status: 404, json: { code: "NotFound" } });
    if (route.request().method() === "PUT" && rejectScheduleOnce) { rejectScheduleOnce = false; return route.fulfill({ status: 412, json: { code: "StaleVersion" } }); }
    if (route.request().method() === "PUT") { scheduleExists = true; return route.fulfill({ status: 202, json: { commandId: "schedule-command", targetId: jobId, state: "Pending" } }); }
    if (route.request().method() === "DELETE" && options.deleteFails) return route.fulfill({ status: 503, json: { code: "Unavailable" } });
    if (route.request().method() === "DELETE") { scheduleExists = false; deleteConfirmed = true; return route.fulfill({ status: 202, json: { commandId: "delete-command", targetId: jobId } }); }
    if (route.request().method() === "GET" && deleteConfirmed && options.scheduleReadFailsAfterDelete) return route.fulfill({ status: 503, json: { code: "Unavailable" } });
    if (!scheduleExists) return route.fulfill({ status: 404, json: { code: "NotFound" } });
    return route.fulfill({ json: { input: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null }, policy: { mode: "Skip" }, paused } });
  });
  await page.route("**/api/schedules/preview", route => route.fulfill({ json: [{ utc: "2026-11-02T14:00:00Z", offset: "-05:00:00" }] }));
  await page.route("**/api/template-validation", route => route.fulfill({ json: { issues: [] } }));
  await page.route("**/api/jobs/*/actions", async route => {
    const path = new URL(route.request().url()).pathname;
    const body = route.request().postDataJSON();
    requests.push({ method: "POST", path, body, etag: route.request().headers()["if-match"] });
    if (body.action === "Enable" && rejectEnableOnce) { rejectEnableOnce = false; etag = '"current-enable"'; return route.fulfill({ status: 412, json: { code: "StaleVersion" } }); }
    if (body.action === "Enable") enabled = true;
    if (body.action === "Disable" || body.action === "Archive") enabled = false;
    if (body.action === "PauseSchedule") paused = true;
    if (body.action === "ResumeSchedule") paused = false;
    if (body.action === "Duplicate") duplicates.add(body.targetJobId);
    return route.fulfill({ status: 202, json: { commandId: "action-command", targetId: body.targetJobId ?? jobId, state: "Pending" } });
  });
  await page.route(/\/api\/jobs\/[^/]+$/, async route => {
    const targetId = new URL(route.request().url()).pathname.split("/").at(-1) ?? jobId;
    const body = route.request().postDataJSON();
    requests.push({ method: "PUT", path: new URL(route.request().url()).pathname, body, etag: route.request().headers()["if-match"] });
    if (body.name === "Edited name stays after conflict" && rejectEditedNameOnce) { rejectEditedNameOnce = false; etag = '"current-save"'; return route.fulfill({ status: 412, json: { code: "StaleVersion" } }); }
    return route.fulfill({ status: 202, json: { commandId: "edit-command", targetId } });
  });
  let firstRun = true;
  await page.route("**/api/jobs/*/runs", async route => {
    requests.push({ method: "POST", path: route.request().url(), body: route.request().postDataJSON() });
    if (firstRun) { firstRun = false; return route.abort(); }
    return route.fulfill({ status: 202, json: { targetId: "22222222-2222-4222-8222-222222222222", commandId: "run-command", state: "Pending" } });
  });
  await page.route("**/api/executions/*/stop", route => { requests.push({ method: "POST", path: route.request().url(), body: route.request().postDataJSON() }); return route.fulfill({ status: 202, json: { commandId: "stop-command" } }); });
  await page.goto("/#/jobs/new");
  await expect(page.getByRole("heading", { name: "Create job" })).toBeVisible();
  return { requests, rejectEnable: () => { rejectEnableOnce = true; } };
}

async function createScheduledDraft(page: import("@playwright/test").Page, name: string) {
  await page.getByLabel("Job name").fill(name);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByLabel("Use a schedule").check();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
}

test("guided create, stale edit, lifecycle, duplicate, retry and confirmed force stop", async ({ page }) => {
  const { requests, rejectEnable } = await setup(page);
  await page.getByLabel("Job name").fill("Demo");
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByLabel("Use a schedule").check();
  await page.getByRole("button", { name: "Preview next runs" }).click();
  await expect(page.getByText(/2026-11-02T14:00:00Z/)).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByText("No timeout", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  const failedSchedule = page.waitForResponse(response => response.url().endsWith(`/api/jobs/${jobId}/schedule`) && response.request().method() === "PUT");
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  expect((await failedSchedule).status()).toBe(412);
  await expect(page.getByText(/Status: Disabled/)).toBeVisible();
  expect(requests.filter(request => request.path === "/api/jobs")).toHaveLength(1);
  await expect(page.getByRole("button", { name: "Load current revision" })).toBeVisible();
  await page.getByRole("button", { name: "Load current revision" }).click();
  await page.getByRole("button", { name: "Restore my draft" }).click();
  await page.getByRole("button", { name: "I reviewed this current revision" }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
  expect(requests.some(request => request.path.endsWith("/schedule") && request.method === "PUT")).toBe(true);

  await page.getByRole("tab", { name: "Schedule", exact: true }).click();
  await page.getByLabel("Use a schedule").uncheck();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  const scheduleDeletion = page.waitForResponse(response => response.url().endsWith(`/api/jobs/${jobId}/schedule`) && response.request().method() === "DELETE");
  const scheduleConfirmation = page.waitForResponse(response => response.url().endsWith(`/api/jobs/${jobId}/schedule`) && response.request().method() === "GET");
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await Promise.all([scheduleDeletion, scheduleConfirmation]);
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
  const deletionIndex = requests.findIndex(request => request.method === "DELETE" && request.path.endsWith("/schedule"));
  expect(deletionIndex).toBeGreaterThanOrEqual(0);
  expect(requests[deletionIndex].etag).toBe('"initial"');
  expect(requests.slice(deletionIndex + 1).some(request => request.method === "GET" && request.path.endsWith("/schedule"))).toBe(true);

  await page.getByRole("tab", { name: "Template", exact: true }).click();
  await page.getByLabel("Job name").fill("Edited name stays after conflict");
  const stale = page.waitForResponse(response => response.url().endsWith(`/api/jobs/${jobId}`) && response.request().method() === "PUT");
  for (let step = 0; step < 4; step++) await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  expect((await stale).status()).toBe(412);
  await page.getByRole("tab", { name: "Template", exact: true }).click();
  await expect(page.getByLabel("Job name")).toHaveValue("Edited name stays after conflict");
  await expect(page.getByRole("button", { name: "Load current revision" })).toBeVisible();
  await page.getByRole("button", { name: "Load current revision" }).click();
  await page.getByRole("tab", { name: "Template", exact: true }).click();
  await expect(page.getByLabel("Job name")).toHaveValue("Demo");
  for (let step = 0; step < 4; step++) await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Restore my draft" }).click();
  await page.getByRole("tab", { name: "Template", exact: true }).click();
  await expect(page.getByLabel("Job name")).toHaveValue("Edited name stays after conflict");
  for (let step = 0; step < 4; step++) await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "I reviewed this current revision" }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  expect(requests.filter(request => request.method === "PUT" && request.path === `/api/jobs/${jobId}`).at(-1)?.etag).toBe('"current-save"');
  await page.getByRole("button", { name: "Next", exact: true }).click();

  rejectEnable();
  await page.getByRole("button", { name: "Enable job" }).click();
  await expect(page.getByRole("button", { name: "Load current revision" })).toBeVisible();
  const staleEnableCount = requests.filter(request => request.body?.action === "Enable").length;
  await expect(page.getByRole("button", { name: "Enable job" })).toBeDisabled();
  expect(requests.filter(request => request.body?.action === "Enable")).toHaveLength(staleEnableCount);
  await page.getByRole("button", { name: "Load current revision" }).click();
  await page.getByRole("button", { name: "I reviewed this current revision" }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Enable job" }).click();
  expect(requests.filter(request => request.body?.action === "Enable").at(-1)?.etag).toBe('"current-enable"');
  await page.getByRole("button", { name: "Run now" }).click();
  const runNowDialog = page.getByRole("dialog");
  await runNowDialog.getByRole("button", { name: "Run now" }).click();
  await expect(page.getByText(/Unable to complete the request/)).toBeVisible();
  await runNowDialog.getByRole("button", { name: "Run now" }).click();
  const runKeys = requests.filter(request => request.path.endsWith("/runs")).map(request => request.body.requestKey);
  expect(runKeys).toHaveLength(2);
  expect(runKeys[0]).toBe(runKeys[1]);

  await page.getByRole("button", { name: "Disable" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Disable" }).click();
  await expect(page.getByRole("button", { name: "Enable", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Enable", exact: true }).click();
  await page.getByRole("tab", { name: "Activation", exact: true }).click();
  await page.getByRole("button", { name: "Stop execution" }).click();
  await page.getByLabel("Force stop").check();
  await page.getByLabel("Reason").fill("Operator requested");
  await page.getByRole("dialog").getByRole("button", { name: "Confirm stop" }).click();
  await expect(page.getByRole("dialog")).toContainText("remote outcome");
  expect(requests.some(request => request.path.includes("/executions/") && request.body.force === true && request.body.confirmForce === true)).toBe(true);
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
  await page.getByRole("button", { name: "Duplicate" }).click();
  await expect(page.getByText("Review this disabled copy before enabling it.")).toBeVisible();
  await expect(page.getByText(/Status: Disabled/)).toBeVisible();
  expect(requests.some(request => request.body?.action === "Duplicate" && request.body.targetJobId)).toBe(true);
});

test("viewer has no configuration or management controls", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.goto("/#/monitoring");
  await expect(page.getByRole("heading", { name: "Job monitoring" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Jobs" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /run now|duplicate|disable|archive/i })).toHaveCount(0);
  await expect(page.getByLabel(/configuration/i)).toHaveCount(0);
});

test("job wizard controls fit desktop and narrow keyboard navigation", async ({ page }) => {
  await setup(page);
  for (const width of [375, 1280]) {
    await page.setViewportSize({ width, height: 850 });
    await page.keyboard.press("Tab");
    await page.keyboard.press("Tab");
    await expect(page.getByRole("heading", { name: "Create job" })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  }
});

test("failed schedule DELETE stays truthful and keeps safety actions while blocking manual enable", async ({ page }) => {
  await setup(page, { deleteFails: true, rejectScheduleOnce: false });
  await createScheduledDraft(page, "DELETE fails");
  await page.getByRole("tab", { name: "Schedule", exact: true }).click();
  await page.getByLabel("Use a schedule").uncheck();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByRole("alert").filter({ hasText: /schedule removal could not be confirmed/i }).last()).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: /schedule removal could not be confirmed/i }).last()).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause schedule" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Disable" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Enable job" })).toBeDisabled();
});

test("successful schedule DELETE with failed confirming GET remains unresolved and blocks enable", async ({ page }) => {
  await setup(page, { scheduleReadFailsAfterDelete: true, rejectScheduleOnce: false });
  await createScheduledDraft(page, "GET fails");
  await page.getByRole("tab", { name: "Schedule", exact: true }).click();
  await page.getByLabel("Use a schedule").uncheck();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByRole("alert").filter({ hasText: /schedule removal could not be confirmed/i }).last()).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByRole("button", { name: "Pause schedule" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Disable" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Enable job" })).toBeDisabled();
});

test("scheduled source duplicate derives its own no-schedule state and can be enabled manually", async ({ page }) => {
  const { requests } = await setup(page, { rejectScheduleOnce: false });
  await createScheduledDraft(page, "Scheduled source");
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Duplicate" }).click();
  await expect(page.getByText("Review this disabled copy before enabling it.")).toBeVisible();
  await expect(page.getByText("Manual job")).toBeVisible();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByRole("button", { name: "Enable job" })).toBeEnabled();
  expect(requests.some(request => request.method === "GET" && request.path.includes("/api/jobs/") && request.path.endsWith("/schedule"))).toBe(true);
});

test("durable inventory restores archived view and literal search from the route", async ({ page }) => {
  await setup(page);
  await page.route("**/api/jobs*", route => route.request().method() === "GET"
    ? route.fulfill({ json: { items: [{ jobId, name: "Café_100%", templateId: "sample", templateVersion: "1", enabled: false, archived: true, scheduleKind: null, scheduleZoneId: null, schedulePaused: false, configurationUpdatedUtc: "2020-01-01T00:00:00Z" }], nextCursor: null } })
    : route.continue());
  await page.goto("/#/jobs?view=archived&search=Caf%C3%A9_%25");
  await expect(page.getByText("Café_100%")).toBeVisible();
  await expect(page.getByRole("combobox", { name: "View" })).toContainText("Archived");
  await page.reload();
  await expect(page.getByText("Café_100%")).toBeVisible();
});
