import { expect, test } from "@playwright/test";

test("viewer can inspect incident and delivery monitoring without management controls", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/alerts/incidents**", route => route.fulfill({ json: [{ incidentId: "11111111-1111-4111-8111-111111111111", ruleName: "Failures", condition: "RepeatedFailure", subjectKey: "job:22222222-2222-4222-8222-222222222222", state: "Open", openedAtUtc: "2026-10-18T12:00:00Z", lastObservedAtUtc: "2026-10-18T12:01:00Z", resolvedAtUtc: null, safeCode: null }] }));
  await page.route("**/api/alerts/deliveries**", route => route.fulfill({ json: [{ deliveryId: "33333333-3333-4333-8333-333333333333", incidentId: "11111111-1111-4111-8111-111111111111", kind: "Open", status: "Delivered", attemptCount: 1, nextAvailableAtUtc: "2026-10-18T12:00:00Z", completedAtUtc: "2026-10-18T12:00:01Z", safeCode: null }] }));

  await page.goto("/");
  await page.getByRole("link", { name: "Alerts" }).click();
  await expect(page.getByRole("table", { name: "Incidents" })).toBeVisible();
  await expect(page.getByText("RepeatedFailure")).toBeVisible();
  await expect(page.getByRole("table", { name: "Deliveries" })).toBeVisible();
  await expect(page.getByText("Delivered")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Alert rules" })).toHaveCount(0);
});
