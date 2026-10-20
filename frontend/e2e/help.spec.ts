import { expect, test } from "@playwright/test";

async function openApp(page: import("@playwright/test").Page, hash: string) {
  const documentationRequests: string[] = [];
  page.on("request", request => {
    if (/help|\.md(?:\?|$)/i.test(request.url())) documentationRequests.push(request.url());
  });
  await page.route("**/api/**", route => route.fulfill({ json: {} }));
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: false,
    canRead: true,
    canManage: true,
    mode: "Anonymous",
    language: "en",
    timeZone: "UTC",
  } }));
  await page.goto(`/${hash}`);
  return documentationRequests;
}

test("English readers can open, search, and reach a contextual guide without documentation fetches", async ({ page }) => {
  const documentationRequests = await openApp(page, "#/help");
  await expect(page.getByRole("link", { name: "Getting started" })).toBeVisible();
  await page.getByRole("searchbox", { name: "Search help" }).fill("schedule");
  await page.getByRole("link", { name: "Manage schedules" }).click();
  await expect(page).toHaveURL(/#\/help:schedules$/);
  await expect(page.getByRole("heading", { name: "Manage schedules" })).toBeVisible();

  await page.goto("/#/help:jobs");
  await expect(page.getByRole("heading", { name: "Create and manage jobs" })).toBeVisible();

  await page.goto("/#/dashboard");
  await page.getByRole("button", { name: "Help for this page" }).click();
  await expect(page).toHaveURL(/#\/help:dashboard$/);
  await expect(page.getByRole("heading", { name: "Understand the dashboard" })).toBeVisible();
  expect(documentationRequests).toEqual([]);
});

test("Portuguese locale switching preserves a directly linked topic", async ({ page }) => {
  await openApp(page, "#/help:settings");
  await expect(page.getByRole("heading", { name: "Manage tenant settings" })).toBeVisible();
  await page.getByRole("combobox", { name: "Language" }).selectOption("pt-BR");
  await expect(page).toHaveURL(/#\/help:settings$/);
  await expect(page.getByRole("heading", { name: "Gerencie as configurações do tenant" })).toBeVisible();
  await expect(page.getByText("nome de exibição", { exact: false })).toBeVisible();
});
