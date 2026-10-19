import { expect, test } from "@playwright/test";

const dashboard = {
  instanceId: "11111111-1111-4111-8111-111111111111", observedUtc: "2026-10-18T12:00:00Z", overall: "Healthy",
  queued: 2, running: 1, needsReview: 0, succeeded24h: 4, failed24h: 1, longRunning: 0, upcoming: 0,
  readyWorkers: 1, totalWorkers: 1, desiredCapacityRevision: 3, effectiveCapacityRevision: 3,
  capacityState: "Applied", pendingProjections: 0, upcomingItems: [],
  recentItems: [{ executionId: "22222222-2222-4222-8222-222222222222", jobId: "33333333-3333-4333-8333-333333333333", jobName: "Nightly report", state: "Succeeded", createdAtUtc: "2026-10-18T11:00:00Z", correlationId: "44444444-4444-4444-8444-444444444444" }],
};

test.beforeEach(async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: dashboard }));
  await page.route("**/api/monitoring/jobs**", route => route.fulfill({ json: { items: [{ jobId: "33333333-3333-4333-8333-333333333333", revision: 2, name: "Nightly report", templateId: "report", templateVersion: "1", enabled: true, archived: false, hasSchedule: true, schedulePaused: false, activeExecutions: 0, failed24h: 1, createdAtUtc: "2026-10-01T00:00:00Z" }], nextCursor: null } }));
  await page.route("**/api/monitoring/workers", route => route.fulfill({ json: [] }));
  await page.route("**/api/audit**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
});

test("viewer can inspect safe dashboard and paged job history", async ({ page }) => {
  await page.goto("/#/dashboard");
  await expect(page.getByText("Nightly report", { exact: false }).first()).toBeVisible();
  await page.getByRole("link", { name: "Monitoring" }).click();
  await expect(page.getByRole("table", { name: "Jobs" })).toBeVisible();
  await expect(page.getByText("report · 1")).toBeVisible();
  await expect(page.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("worker and audit routes expose read-only operational views", async ({ page }) => {
  await page.goto("/#/workers");
  await expect(page.getByRole("table", { name: "Workers" })).toBeVisible();
  await expect(page.getByText("No workers registered.")).toBeVisible();
  await page.getByRole("link", { name: "Audit" }).click();
  await expect(page.getByRole("table", { name: "Audit" })).toBeVisible();
});
