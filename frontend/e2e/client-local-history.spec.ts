import { expect, test } from "@playwright/test";

test.use({ timezoneId: "America/Sao_Paulo" });

test("audit custom day sends local-midnight UTC bounds", async ({ page }) => {
  test.setTimeout(60_000);
  const auditQueries: URL[] = [];
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/audit**", route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/event-types")) return route.fulfill({ json: [] });
    auditQueries.push(url);
    return route.fulfill({ json: { items: [], nextCursor: null } });
  });

  await page.bringToFront();
  await page.waitForFunction(() => document.visibilityState === "visible");
  await page.goto("/#/audit");
  await page.bringToFront();
  await page.waitForFunction(() => document.visibilityState === "visible");
  await expect(page.getByRole("table", { name: "Audit" })).toBeVisible();
  await page.getByRole("button", { name: "Custom" }).click();
  await page.getByLabel("Start date").fill("2026-10-30");
  await page.getByLabel("End date").fill("2026-10-30");

  await expect.poll(() => auditQueries.some(query =>
    query.searchParams.get("fromUtc") === "2026-10-30T03:00:00.000Z" &&
    query.searchParams.get("toUtc") === "2026-10-31T03:00:00.000Z",
  ), { timeout: 15_000 }).toBe(true);
});
