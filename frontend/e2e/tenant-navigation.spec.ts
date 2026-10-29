import { expect, test } from "@playwright/test";

test("grouped navigation and long tenant name remain usable on a narrow viewport", async ({ page }) => {
  const tenantName = "North Region Operations and Customer Support Team";
  await page.setViewportSize({ width: 360, height: 780 });
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/settings", route => route.fulfill({ json: {
    displayName: tenantName, displayTimeZone: "UTC", version: "AQAAAAAAAAA=",
  }, headers: { ETag: '"AQAAAAAAAAA="' } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: {
    instanceId: "11111111-1111-4111-8111-111111111111", observedUtc: "2026-10-18T12:00:00Z", overall: "Healthy",
    queued: 0, running: 0, needsReview: 0, succeeded24h: 0, failed24h: 0, longRunning: 0, upcoming: 0,
    readyWorkers: 0, totalWorkers: 0, desiredCapacityRevision: 0, effectiveCapacityRevision: 0, capacityState: "Applied",
    upcomingItems: [], recentItems: [],
  } }));
  await page.goto("/#/dashboard");

  await expect(page.getByText(tenantName, { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible();
  for (const heading of ["Monitoring", "Maintenance", "Administration"]) await expect(page.getByText(heading, { exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Workers" }).click();
  await expect(page).toHaveURL(/#\/workers$/);
  await page.getByRole("link", { name: "Templates" }).click();
  await expect(page).toHaveURL(/#\/templates$/);
  await page.getByRole("link", { name: "Settings" }).click();
  await expect(page).toHaveURL(/#\/settings$/);
  await expect(page.getByRole("button", { name: "Personal settings" })).toBeVisible();

  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});
