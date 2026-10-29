import { expect, test } from "@playwright/test";

test("manager creates a webhook sink, creates a disabled rule, then enables it", async ({ page }) => {
  const sinkId = "22222222-2222-4222-8222-222222222222";
  const ruleId = "44444444-4444-4444-8444-444444444444";
  let sink: Record<string, any> | undefined;
  let rule: Record<string, any> | undefined;
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/alerts/rule-options**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/alerts/incidents**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/alerts/deliveries**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "csrf" } }));
  await page.route("**/api/alert-sinks", route => route.fulfill({ json: sink ? [sink] : [] }));
  await page.route("**/api/alert-rules", route => route.fulfill({ json: rule ? [rule] : [] }));
  await page.route(`**/api/alert-sinks/${sinkId}`, async route => {
    if (route.request().method() === "PUT") {
      sink = { ...route.request().postDataJSON(), version: "sink-v1" };
      return route.fulfill({ json: { definition: sink, version: "sink-v1" } });
    }
    return route.fulfill({ json: sink, headers: { ETag: '"sink-v1"' } });
  });
  await page.route(`**/api/alert-rules/${ruleId}`, async route => {
    if (route.request().method() === "PUT") {
      const body = route.request().postDataJSON();
      rule = { ...body, version: body.enabled ? "rule-v2" : "rule-v1" };
      return route.fulfill({ json: { rule, version: rule.version } });
    }
    return route.fulfill({ json: rule, headers: { ETag: `"${rule?.version}"` } });
  });
  await page.addInitScript(({ sinkId, ruleId }) => {
    let next = 0;
    Object.defineProperty(crypto, "randomUUID", { configurable: true, value: () => next++ === 0 ? sinkId : ruleId });
  }, { sinkId, ruleId });

  await page.goto("/");
  await page.getByRole("link", { name: "Alerts" }).click();
  await page.getByRole("button", { name: "New webhook sink" }).click();
  await page.getByRole("textbox", { name: "Sink name" }).fill("Ops webhook");
  await page.getByRole("textbox", { name: "Webhook URL" }).fill("https://example.test/alerts");
  await page.getByRole("button", { name: "Save" }).last().click();
  await expect(page.getByRole("alert")).toContainText("Webhook sink created");
  await expect(page.getByRole("button", { name: "New alert rule" })).toBeEnabled();
  await expect(page.getByRole("textbox", { name: "Sink name" })).toHaveValue("Ops webhook");

  await page.getByRole("button", { name: "New alert rule" }).click();
  await page.getByRole("textbox", { name: "Rule name" }).fill("Repeated failures");
  await page.getByRole("button", { name: "Create rule" }).click();
  await expect(page.getByRole("button", { name: "Enable" })).toBeVisible();
  expect(rule?.enabled).toBe(false);
  await page.getByRole("button", { name: "Enable" }).click();
  await expect.poll(() => rule?.enabled).toBe(true);
});

test("viewer can inspect incident and delivery monitoring without management controls", async ({ page }) => {
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/alerts/rule-options**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route("**/api/alerts/incidents**", route => route.fulfill({ json: { items: [{ incidentId: "11111111-1111-4111-8111-111111111111", ruleId: "44444444-4444-4444-8444-444444444444", ruleName: "Failures", condition: "RepeatedFailure", subjectKey: "job:22222222-2222-4222-8222-222222222222", state: "Open", openedAtUtc: "2026-10-18T12:00:00Z", lastObservedAtUtc: "2026-10-18T12:01:00Z", resolvedAtUtc: null, safeCode: null }], nextCursor: null } }));
  await page.route("**/api/alerts/deliveries**", route => route.fulfill({ json: { items: [{ deliveryId: "33333333-3333-4333-8333-333333333333", incidentId: "11111111-1111-4111-8111-111111111111", ruleName: "Failures", subjectKey: "job:22222222-2222-4222-8222-222222222222", kind: "Open", status: "Delivered", attemptCount: 1, nextAvailableAtUtc: "2026-10-18T12:00:00Z", completedAtUtc: "2026-10-18T12:00:01Z", safeCode: null }], nextCursor: null } }));

  await page.goto("/");
  const incidentsResponse = page.waitForResponse(response => response.url().includes("/api/alerts/incidents") && response.ok());
  const deliveriesResponse = page.waitForResponse(response => response.url().includes("/api/alerts/deliveries") && response.ok());
  await page.getByRole("link", { name: "Alerts" }).click();
  await Promise.all([incidentsResponse, deliveriesResponse]);
  await expect(page.getByRole("table", { name: "Incidents" })).toBeVisible();
  await expect(page.getByRole("cell", { name: "Failures", exact: true })).toBeVisible();
  await expect(page.getByRole("table", { name: "Deliveries" })).toBeVisible();
  await expect(page.getByText("Delivered")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Alert rules" })).toHaveCount(0);
});
