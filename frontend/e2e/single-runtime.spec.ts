import { expect, test } from "@playwright/test";

const runtimeDashboard = {
  instanceId: "11111111-1111-4111-8111-111111111111",
  observedUtc: "2026-10-23T12:00:00Z",
  overall: "Healthy",
  queued: 2,
  running: 1,
  needsReview: 1,
  succeeded24h: 4,
  failed24h: 1,
  longRunning: 0,
  upcoming: 2,
  runtimeReady: true,
  hostCeiling: 4,
  runtimeHeartbeatUtc: "2026-10-23T12:00:00Z",
  upcomingItems: [],
  recentItems: [],
};

test("visible operator views use the single-runtime vocabulary", async ({ page }) => {
  await page.route("**/api/**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: runtimeDashboard }));
  await page.route("**/api/settings", route => route.fulfill({ json: {
    displayName: "North tenant", displayTimeZone: "UTC", version: "AQAAAAAAAAA=",
  }, headers: { ETag: '"AQAAAAAAAAA="' } }));
  await page.route("**/api/monitoring/jobs**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/monitoring/workers", route => route.fulfill({ json: {
    bootId: "22222222-2222-4222-8222-222222222222", ready: true, hostCeiling: 4, lastHeartbeatUtc: "2026-10-23T12:00:00Z",
  } }));
  await page.route("**/api/audit**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/alerts/incidents**", route => route.fulfill({ json: [] }));
  await page.route("**/api/alerts/deliveries**", route => route.fulfill({ json: [] }));
  await page.route("**/api/alert-rules**", route => route.fulfill({ json: [] }));
  await page.route("**/api/alert-sinks**", route => route.fulfill({ json: [] }));

  const verify = async () => {
    await expect(page.locator("body")).not.toContainText(/Quartz|projection/i);
  };
  const navigate = async (route: string) => {
    await page.evaluate(value => {
      window.history.replaceState(null, "", `#/${value}`);
      window.dispatchEvent(new HashChangeEvent("hashchange"));
    }, route);
  };

  await page.goto("/#/dashboard");
  await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
  await expect(page.getByText("Ready")).toBeVisible();
  await expect(page.getByText("4", { exact: true }).first()).toBeVisible();
  await verify();

  for (const route of [
    ["monitoring", "Job monitoring"],
    ["workers", "Workers"],
    ["alerts", "Alerts"],
    ["audit", "Audit"],
    ["jobs", "Jobs"],
  ]) {
    await navigate(route[0]);
    await expect(page.getByRole("heading", { name: route[1], level: 1 })).toBeVisible();
    if (route[0] === "workers") {
      await expect(page.getByText("Ready")).toBeVisible();
      await expect(page.getByText("4", { exact: true })).toBeVisible();
    }
    await verify();
  }

  await navigate("settings");
  await expect(page.getByRole("heading", { name: "Settings", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: "Retention", exact: true }).click();
  await verify();

  await navigate("help");
  await verify();
});
