import { expect, test } from "@playwright/test";

const dashboard = {
  instanceId: "11111111-1111-4111-8111-111111111111",
  observedUtc: "2026-10-18T12:00:00Z",
  overall: "Degraded",
  queued: 0,
  running: 0,
  needsReview: 1,
  succeeded24h: 2,
  failed24h: 3,
  longRunning: 0,
  upcoming: 0,
  readyWorkers: 0,
  totalWorkers: 1,
  desiredCapacityRevision: 2,
  effectiveCapacityRevision: 2,
  capacityState: "Applied",
  pendingProjections: 0,
  upcomingItems: [],
  recentItems: [],
};

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.clear());
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: dashboard }));
  await page.route("**/api/monitoring/jobs**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
});

test("dashboard supports keyboard navigation and shows health without color cues", async ({ page }) => {
  await page.goto("/#/dashboard");

  await expect(page.getByRole("main")).toBeVisible();
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
  await expect(page.getByText("Degraded", { exact: true })).toBeVisible();
  await expect(page.getByText("Failed 24h", { exact: true })).toBeVisible();

  const monitoring = page.getByRole("link", { name: "Monitoring" });
  await monitoring.focus();
  await expect(monitoring).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("table", { name: "Jobs" })).toBeVisible({ timeout: 15_000 });
});

test("dashboard remains usable on mobile and switches accessible labels to Brazilian Portuguese", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/dashboard");
  await expect(page.getByRole("main")).toBeVisible();

  const language = page.getByRole("combobox", { name: "Language" });
  await language.selectOption("pt-BR");

  await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
  await expect(page.getByText("Estado:", { exact: false })).toBeVisible();
  await expect(page.getByText("Nenhuma execução recente.")).toBeVisible();
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});
