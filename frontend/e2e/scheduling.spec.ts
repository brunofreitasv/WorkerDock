import { expect, test } from "@playwright/test";

test("switching locale preserves the selected repeated-hour instant and previews offsets", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "test-csrf" } }));
  await page.route("**/api/templates", route => route.fulfill({ json: { items: [{ selectable: true, descriptor: { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } } }] } }));
  await page.route("**/api/time-zones", route => route.fulfill({ json: ["America/New_York", "UTC"] }));
  await page.route("**/api/schedules/preview", route => route.fulfill({ json: [{ utc: "2026-11-01T05:30:00Z", offset: "-04:00" }, { utc: "2026-11-01T06:30:00Z", offset: "-05:00" }] }));
  await page.goto("/#/jobs");
  await expect(page.getByRole("heading", { name: /create job/i })).toBeVisible();
  await page.getByRole("button", { name: /schedule/i }).click();
  await page.getByLabel("Schedule kind").click();
  await page.getByRole("option", { name: "One time" }).click();
  await page.getByLabel("Time zone").click();
  await page.getByRole("option", { name: "America/New_York" }).click();
  await page.getByLabel(/local date and time/i).fill("2026-11-01T01:30");
  await page.getByLabel(/utc offset/i).fill("-05:00");
  await page.getByRole("button", { name: /preview/i }).click();
  const before = await page.getByTestId("resolved-utc").inputValue();
  await page.getByLabel("Language").selectOption("pt-BR");
  await expect(page.getByTestId("resolved-utc")).toHaveValue(before);
  await expect(page.getByText("2026-11-01T06:30:00Z")).toBeVisible();
  await expect(page.getByText("-05:00").last()).toBeVisible();
});
