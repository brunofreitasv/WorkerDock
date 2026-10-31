import { expect, test } from "@playwright/test";

test.use({ timezoneId: "America/Sao_Paulo" });

test("dashboard and execution history show instants in client time despite legacy UTC settings", async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem("workerdock.timeZone", "UTC"));
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: {
    instanceId: "tenant", observedUtc: "2026-10-30T03:15:00Z", overall: "Healthy", queued: 0, running: 0, needsReview: 0,
    succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 1, runtimeReady: true, hostCeiling: 4, runtimeHeartbeatUtc: "2026-10-30T03:15:00Z",
    upcomingItems: [{ jobId: "next", jobName: "Next job", templateId: "http", scheduledForUtc: "2026-10-30T03:15:00Z" }],
    recentItems: [{ executionId: "recent", jobId: "job", jobName: "Recent job", state: "Succeeded", createdAtUtc: "2026-10-30T01:15:00Z", correlationId: "corr", templateId: "http", duration: "00:01:00" }],
  } }));
  await page.route("**/api/monitoring/executions**", route => route.fulfill({ json: { items: [{ executionId: "recent", jobId: "job", jobName: "Recent job", templateId: "http", state: "Succeeded", triggerKind: "Schedule", createdAtUtc: "2026-10-30T01:15:00Z", startedAtUtc: "2026-10-30T03:15:00Z", duration: "00:01:00" }], nextCursor: null } }));
  await page.goto("/#/dashboard");
  await page.bringToFront();
  await expect(page.getByRole("table", { name: "Recent executions" }).getByText("10/29/2026, 10:15:00 PM")).toBeVisible();
  await expect(page.getByText("Next job \u00b7 http \u00b7 10/30/2026, 12:15:00 AM")).toBeVisible();
  await page.goto("/#/executions");
  await expect(page.getByRole("table", { name: "Execution history" }).getByText("10/29/2026, 10:15:00 PM")).toBeVisible();
});
