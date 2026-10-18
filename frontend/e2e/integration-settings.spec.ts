import { expect, test } from "@playwright/test";

test("manager edits scoped outbound settings and sees hard-denial guidance", async ({ page }) => {
  let policy = { name: "tenant-jobs", hosts: ["api.example.test"], ports: [443], privateCidrs: [], allowHttp: false, version: "AQAAAAAAAAA=" };
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "test-token" } }));
  await page.route("**/api/settings", route => route.fulfill({ json: { displayName: "Test", displayTimeZone: "UTC", version: "AQAAAAAAAAA=" }, headers: { ETag: '"AQAAAAAAAAA="' } }));
  await page.route("**/api/settings/egress/tenant-jobs", async route => {
    if (route.request().method() === "GET") return route.fulfill({ json: policy, headers: { ETag: `"${policy.version}"` } });
    const body = route.request().postDataJSON() as typeof policy;
    policy = { ...policy, ...body, version: "AgAAAAAAAAA=" };
    return route.fulfill({ json: policy, headers: { ETag: `"${policy.version}"` } });
  });
  await page.goto("/");
  await page.getByRole("link", { name: "Settings" }).click();
  await page.getByRole("button", { name: "Outbound policy" }).click();
  await expect(page.getByLabel("Allowed hosts")).toHaveValue("api.example.test");
  await expect(page.getByText(/Deployment hard denials cannot be changed here/)).toBeVisible();
  await page.getByLabel("Allowed hosts").fill("api.example.test\n*.hooks.example.test");
  await page.getByRole("button", { name: "Save" }).last().click();
  await expect(page.getByRole("alert").filter({ hasText: "Settings saved" })).toBeVisible();
  expect(policy.hosts).toEqual(["api.example.test", "*.hooks.example.test"]);
});

test("viewer does not receive the management settings navigation", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Settings" })).toHaveCount(0);
});
