import { expect, test } from "@playwright/test";

const dashboard = {
  instanceId: "11111111-1111-4111-8111-111111111111", observedUtc: "2026-10-18T12:00:00Z", overall: "Healthy",
  queued: 2, running: 1, needsReview: 0, succeeded24h: 4, failed24h: 1, longRunning: 0, upcoming: 0,
  readyWorkers: 1, totalWorkers: 1, desiredCapacityRevision: 3, effectiveCapacityRevision: 3,
  capacityState: "Applied", upcomingItems: [],
  recentItems: [{ executionId: "22222222-2222-4222-8222-222222222222", jobId: "33333333-3333-4333-8333-333333333333", jobName: "Nightly report", state: "Succeeded", createdAtUtc: "2026-10-18T11:00:00Z", correlationId: "44444444-4444-4444-8444-444444444444", templateId: "report", duration: "00:00:42" }],
};

test.beforeEach(async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: dashboard }));
  await page.route("**/api/monitoring/jobs**", route => route.fulfill({ json: { items: [{ jobId: "33333333-3333-4333-8333-333333333333", revision: 2, name: "Nightly report", templateId: "report", templateVersion: "1", enabled: true, archived: false, hasSchedule: true, schedulePaused: false, activeExecutions: 0, failed24h: 1, createdAtUtc: "2026-10-01T00:00:00Z" }], nextCursor: null } }));
  await page.route("**/api/monitoring/workers", route => route.fulfill({ json: [] }));
  await page.route("**/api/monitoring/executions**", route => route.fulfill({ json: { items: [{ executionId: "22222222-2222-4222-8222-222222222222", jobId: "33333333-3333-4333-8333-333333333333", jobName: "Nightly report", templateId: "report", state: "Succeeded", triggerKind: "Schedule", createdAtUtc: "2026-10-18T11:00:00Z", startedAtUtc: "2026-10-18T11:00:01Z", duration: "00:00:42" }], nextCursor: null } }));
  await page.route("**/api/audit**", route => {
    const pathname = new URL(route.request().url()).pathname;
    return pathname.endsWith("/event-types")
      ? route.fulfill({ json: ["JobRevisionSaved", "ManualRunAccepted"] })
      : route.fulfill({ json: { items: [], nextCursor: null } });
  });
});

test("viewer can inspect safe dashboard and paged job history", async ({ page }) => {
  await page.goto("/#/dashboard");
  await expect(page.getByText("Nightly report", { exact: false }).first()).toBeVisible();
  await page.getByRole("link", { name: "Job monitoring" }).click();
  await expect(page.getByRole("table", { name: "Jobs" })).toBeVisible();
  await expect(page.getByText("report · 1")).toBeVisible();
  await expect(page.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("viewer can open full execution history from the Dashboard", async ({ page }) => {
  await page.goto("/#/dashboard");
  await expect(page.getByRole("table", { name: "Recent executions" })).toBeVisible();
  await page.getByRole("button", { name: "View all executions" }).click();
  await expect(page).toHaveURL(/#\/executions$/);
  await expect(page.getByRole("table", { name: "Execution history" })).toBeVisible();
  await expect(page.getByText("Nightly report", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /enable|retry|manage/i })).toHaveCount(0);
});

test("viewer can filter read-only audit history", async ({ page }) => {
  await page.goto("/#/audit");
  const table = page.getByRole("table", { name: "Audit" });
  await expect(table).toBeVisible();
  await expect(page.getByRole("button", { name: "Last 7 days" })).toBeVisible();
  await page.getByRole("combobox", { name: "Event type" }).click();
  await page.getByRole("option", { name: "JobRevisionSaved" }).click();
  await expect(table).toBeVisible();
  await expect(page.getByRole("button", { name: /manage|edit|delete/i })).toHaveCount(0);
});
