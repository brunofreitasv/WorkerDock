import { expect, test, type Page } from "@playwright/test";

const sql = { id: "workerdock.sql-server", version: "1.0.0", displayName: "SQL Server", description: "Execute parameterized SQL", configurationSchema: { type: "object", additionalProperties: false, required: ["profileId", "catalog", "commandKind", "commandText", "parameters", "useTransaction"], properties: { profileId: { type: "string", format: "uuid" }, catalog: { type: "string", minLength: 1 }, commandKind: { type: "string", enum: ["Query", "Procedure"] }, commandText: { type: "string", minLength: 1 }, parameters: { type: "array", items: { type: "object" } }, useTransaction: { type: "boolean" } } } };
const http = { id: "workerdock.http", version: "1.0.0", displayName: "HTTP request", description: "Send a bounded HTTP request", configurationSchema: { type: "object", additionalProperties: false, required: ["method", "url", "query", "headers", "secretHeaders", "parameters", "successStatuses", "maxResponseBytes", "responseMode"], properties: { method: { type: "string", enum: ["GET", "POST"] }, url: { type: "string", minLength: 1 }, query: { type: "object", additionalProperties: { type: "string" } }, headers: { type: "object", additionalProperties: { type: "string" } }, secretHeaders: { type: "object", additionalProperties: { type: "object", properties: { name: { type: "string", "x-workerDock-secretReference": true } } } }, parameters: { type: "object", additionalProperties: { type: ["string", "number", "boolean", "null"] } }, successStatuses: { type: "array", items: { type: "integer" } }, maxResponseBytes: { type: "integer", minimum: 1 }, responseMode: { type: "string", enum: ["Discard", "SafeSummary"], default: "SafeSummary" } } } };
const rows = [{ descriptor: sql, selectable: true }, { descriptor: http, selectable: true }];

async function setup(page: Page) {
  await page.route("**/api/session", (route) => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", (route) => route.fulfill({ json: { requestToken: "test-csrf" } }));
  await page.route("**/api/templates", (route) => route.fulfill({ json: { items: rows } }));
  await page.route("**/api/template-validation", async (route) => {
    const body = route.request().postDataJSON();
    return route.fulfill({ json: { issues: body.configuration.invalid ? [{ path: "$.invalid", code: "AdditionalProperty" }] : [] } });
  });
  await page.route("**/api/jobs", async (route) => route.fulfill({ status: 202, json: { commandId: "00000000-0000-0000-0000-000000000010", targetId: "00000000-0000-0000-0000-000000000020", state: "Applied" } }));
  await page.route("**/api/jobs/*/configuration", (route) => route.fulfill({ json: { revision: {}, version: "AQAAAAAAAAA=", enabled: false, archived: false, concurrencyLimit: 1 }, headers: { ETag: '"AQAAAAAAAAA="' } }));
  await page.route("**/api/jobs/*/enable-manual", (route) => route.fulfill({ status: 202, json: { commandId: "00000000-0000-0000-0000-000000000030", targetId: "job", state: "Applied" } }));
  await page.route("**/api/jobs/*/runs", (route) => route.fulfill({ status: 202, json: { commandId: "run-command", targetId: "execution-123", state: "Pending" } }));
  await page.goto("/#/jobs");
  await page.getByRole("heading", { name: "Manual jobs" }).waitFor();
}

test("SQL manual job saves disabled, validates, enables explicitly and runs with an execution ID", async ({ page }) => {
  await setup(page);
  await page.getByLabel("Job name").fill("Synthetic SQL job");
  await page.getByLabel("Profile ID").fill("11111111-1111-4111-8111-111111111111");
  await page.getByLabel("Catalog").fill("WorkerDockTest");
  await page.getByLabel("Command text").fill("SELECT 1");
  await page.getByRole("button", { name: "Validate configuration" }).click();
  await expect(page.getByText("Configuration is valid.")).toBeVisible();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
  await page.getByRole("button", { name: "Enable manual job" }).click();
  await expect(page.getByText("Manual job enabled.")).toBeVisible();
  await page.getByRole("button", { name: "Run now" }).click();
  await expect(page.getByText("Execution accepted: execution-123")).toBeVisible();
  await expect(page.getByLabel("Password value")).toHaveCount(0);
});

test("HTTP manual job uses metadata fields and secret references only", async ({ page }) => {
  await setup(page);
  await page.getByLabel("Template").selectOption("workerdock.http@1.0.0");
  await page.getByLabel("Job name").fill("Synthetic HTTP job");
  await page.getByLabel("URL").fill("https://api.example.test/health");
  await page.getByLabel("Maximum response bytes").fill("1024");
  await page.getByRole("button", { name: "Add entry" }).click();
  await page.getByLabel("Secret reference").fill("logical.http-token");
  await page.getByRole("button", { name: "Validate configuration" }).click();
  await expect(page.getByText("Configuration is valid.")).toBeVisible();
  await expect(page.getByLabel("Secret reference")).toHaveValue("logical.http-token");
  await expect(page.getByLabel("Password value")).toHaveCount(0);
});

test("412 keeps the unsaved job input available for correction", async ({ page }) => {
  await setup(page);
  await page.getByLabel("Job name").fill("Keep my unsaved name");
  await page.route("**/api/jobs", (route) => route.fulfill({ status: 412, json: { code: "StaleVersion" } }));
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByLabel("Job name")).toHaveValue("Keep my unsaved name");
  await expect(page.getByRole("status").filter({ hasText: "Your unsaved edits are preserved" })).toBeVisible();
});

test("viewer sees registered templates but no job or profile management", async ({ page }) => {
  await page.route("**/api/session", (route) => route.fulfill({ json: { authenticated: true, canRead: true, canManage: false, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Templates" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Jobs" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Settings" })).toHaveCount(0);
});

test("template catalog metadata is rendered as text", async ({ page }) => {
  await setup(page);
  await page.route("**/api/templates", (route) => route.fulfill({ json: { items: [{ descriptor: { ...sql, description: "<img src=x onerror=alert(1)>" }, selectable: true }] } }));
  await page.goto("/#/templates");
  await page.getByLabel("Template").selectOption("workerdock.sql-server@1.0.0");
  await expect(page.getByText("<img src=x onerror=alert(1)>")).toBeVisible();
  await expect(page.locator("img")).toHaveCount(0);
});
