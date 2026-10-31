import { expect, test } from "@playwright/test";

test.use({ timezoneId: "America/Sao_Paulo" });

test("schedule preview shows client and schedule times for both repeated-hour instants", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "test-csrf" } }));
  await page.route("**/api/templates", route => route.fulfill({ json: { items: [{ selectable: true, descriptor: { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } } }] } }));
  await page.route("**/api/time-zones", route => route.fulfill({ json: ["America/New_York", "America/Sao_Paulo", "UTC"] }));
  await page.route("**/api/jobs", route => route.fulfill({ status: 202, json: { targetId: "11111111-1111-4111-8111-111111111111", commandId: "draft" } }));
  await page.route("**/api/schedules/preview", route => route.fulfill({ json: [{ utc: "2026-11-01T05:30:00Z", offset: "-04:00" }, { utc: "2026-11-01T06:30:00Z", offset: "-05:00" }] }));
  await page.goto("/#/jobs/new");
  await expect(page.getByRole("heading", { name: /create job/i })).toBeVisible({ timeout: 15_000 });
  await page.getByLabel("Job name").fill("Draft survives locale change");
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByLabel("Use a schedule").check();
  await page.getByRole("tab", { name: /schedule/i }).click();
  await page.getByLabel("Schedule kind").click();
  await page.getByRole("option", { name: "One time" }).click();
  await page.getByRole("combobox", { name: "Time zone" }).first().click();
  await page.getByRole("option", { name: "America/New_York" }).click();
  await page.getByLabel(/local date and time/i).fill("2026-11-01T01:30");
  await page.getByLabel(/utc offset/i).fill("-05:00");
  await page.getByRole("button", { name: /preview/i }).click();
  const before = await page.getByTestId("resolved-utc").inputValue();
  await expect(page.getByLabel("Local date and time")).toHaveValue("2026-11-01T01:30");
  await expect(page.getByLabel("UTC offset")).toHaveValue("-05:00");
  await expect(page.getByTestId("resolved-utc")).toHaveValue(before);
  await expect(page.getByText("Client time: 11/01/2026, 2:30:00 AM")).toBeVisible();
  await expect(page.getByText("Client time: 11/01/2026, 3:30:00 AM")).toBeVisible();
  await expect(page.getByText(/Schedule time: 11\/01\/2026, 1:30:00 AM.*-04:00/)).toBeVisible();
  await expect(page.getByText(/Schedule time: 11\/01\/2026, 1:30:00 AM.*-05:00/)).toBeVisible();
  await expect(page.getByText("Technical UTC instant: 2026-11-01T06:30:00Z")).toBeVisible();
});
