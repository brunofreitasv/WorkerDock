import { expect, test } from "@playwright/test";

const tenant = {
  id: "tenant-1",
  displayName: "Billing",
  dashboardUrl: "https://billing.example.test/",
  expectedInstanceId: "d6952d61-4075-4cb6-88b4-48189c8f85af",
  health: "Healthy",
  lastAttemptUtc: "2026-10-19T10:00:00Z",
  lastSuccessUtc: "2026-10-19T10:00:00Z",
  version: "AQAAAAAAAQE=",
};

test("viewer can inspect tenant health and follow its independently protected dashboard", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: true, canRead: true, canManage: false, mode: "Authenticated", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/tenants?**", route => route.fulfill({ json: { items: [tenant], nextCursor: null } }));

  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Control Center" })).toBeVisible();
  await expect(page.getByRole("link", { name: /Billing/ })).toHaveAttribute("href", tenant.dashboardUrl);
  await expect(page.getByText("Healthy")).toBeVisible();
  await expect(page.getByRole("button", { name: "Add tenant" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Remove" })).toHaveCount(0);
});

test("manager registration sends a vault reference with antiforgery and reloads the directory", async ({ page }) => {
  let registered: typeof tenant | undefined;
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "csrf-test-token" } }));
  await page.route("**/api/tenants?**", route => route.fulfill({ json: { items: registered ? [registered] : [], nextCursor: null } }));
  await page.route("**/api/tenants", async route => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-workerdock-csrf"]).toBe("csrf-test-token");
    const body = route.request().postDataJSON();
    expect(body.healthCredentialReference).toBe("BILLING_HEALTH");
    expect(JSON.stringify(body)).not.toContain("token-value");
    registered = { ...tenant, displayName: body.displayName, expectedInstanceId: body.expectedInstanceId, dashboardUrl: body.dashboardUrl };
    await route.fulfill({ status: 201, json: registered });
  });

  await page.goto("/");
  await page.getByRole("button", { name: "Add tenant" }).click();
  await page.getByRole("textbox", { name: "Tenant name" }).fill("Billing");
  await page.getByRole("textbox", { name: "Expected instance ID" }).fill(tenant.expectedInstanceId);
  await page.getByRole("textbox", { name: "Dashboard URL" }).fill(tenant.dashboardUrl);
  await page.getByRole("textbox", { name: "Health credential reference" }).fill("BILLING_HEALTH");
  await page.getByRole("button", { name: "Save tenant" }).click();

  await expect(page.getByRole("link", { name: /Billing/ })).toBeVisible();
});
