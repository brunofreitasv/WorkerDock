import { expect, test, type Page } from "@playwright/test";

const sql = {
  id: "workerdock.sql-server", version: "1.1.0", displayName: "SQL Server", description: "Execute a parameterized SQL command",
  configurationSchema: { type: "object", additionalProperties: false, required: ["profileId", "catalog", "commandKind", "commandText", "parameters", "useTransaction"], properties: {
    profileId: { type: "string" }, catalog: { type: "string" }, commandKind: { type: "string", enum: ["Query", "Procedure"] }, commandText: { type: "string" }, useTransaction: { type: "boolean" },
    parameters: { type: "array", items: { type: "object", additionalProperties: false, required: ["name", "dbType", "useExecutionKey", "isNull"], properties: { name: { type: "string" }, dbType: { type: "string", enum: ["Date", "NVarChar"] }, value: { type: ["string", "null"], "x-workerDock-dynamicValue": true }, secret: { type: ["object", "null"] }, useExecutionKey: { type: "boolean" }, isNull: { type: "boolean" } } } },
  } },
};
const http = {
  id: "workerdock.http", version: "1.1.0", displayName: "HTTP request", description: "Send a bounded HTTP request",
  configurationSchema: { type: "object", properties: { query: { type: "object", additionalProperties: { type: "string", "x-workerDock-dynamicValue": true } } } },
};
const jobId = "11111111-1111-4111-8111-111111111111";

async function setup(page: Page, descriptor: typeof sql | typeof http) {
  let revision: any;
  let enabled = false;
  await page.route("**/api/session", route => route.fulfill({ json: { authenticated: false, canRead: true, canManage: true, mode: "Anonymous", language: "en", timeZone: "UTC" } }));
  await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "test-csrf" } }));
  await page.route("**/api/time-zones", route => route.fulfill({ json: ["UTC", "America/Sao_Paulo"] }));
  await page.route("**/api/templates", route => route.fulfill({ json: { items: [{ descriptor, selectable: true }] } }));
  await page.route("**/api/template-validation", route => route.fulfill({ json: { issues: [] } }));
  await page.route("**/api/template-values/preview", async route => {
    const request = route.request().postDataJSON();
    await route.fulfill({ json: { issues: [], values: { [request.dynamicValues.bindings[0].target]: "2026-10-20" }, isExample: true } });
  });
  await page.route("**/api/jobs", async route => {
    revision = route.request().postDataJSON();
    await route.fulfill({ status: 202, json: { commandId: "draft", targetId: jobId, state: "Applied" } });
  });
  await page.route("**/api/jobs/*/configuration", route => route.fulfill({ json: { revision: { ...revision, revision: 1 }, version: "AQAAAAAAAAA=", enabled, archived: false, concurrencyLimit: 1 }, headers: { ETag: '"AQAAAAAAAAA="' } }));
  await page.route("**/api/jobs/*/actions", async route => {
    if (route.request().postDataJSON().action === "Enable") enabled = true;
    await route.fulfill({ status: 202, json: { commandId: "action", targetId: jobId, state: "Applied" } });
  });
  await page.goto("/#/jobs");
  await page.getByRole("heading", { name: "Create job" }).waitFor();
  await page.getByLabel("Job name").fill("Dynamic template job");
  return () => revision;
}

async function save(page: Page) {
  for (let step = 0; step < 3; step++) await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Save disabled draft" }).click();
  await expect(page.getByText("Disabled draft saved.")).toBeVisible();
}

test("SQL Procedure binds a dynamic reference date as a separate parameter", async ({ page }) => {
  const getRevision = await setup(page, sql);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByLabel("Connection profile ID").fill("11111111-1111-4111-8111-111111111111");
  await page.getByLabel("Catalog").fill("WorkerDockTest");
  await page.getByRole("combobox", { name: "Command kind" }).click();
  await page.getByRole("option", { name: "Procedure" }).click();
  await page.getByLabel("Stored procedure name").fill("dbo.Job");
  await page.getByRole("button", { name: "Add parameter" }).click();
  await page.getByLabel("Parameter name").fill("@Dt_Ref");
  await page.getByRole("combobox", { name: "SQL type" }).click();
  await page.getByRole("option", { name: "Date", exact: true }).click();
  await page.getByRole("combobox", { name: "Value source" }).click();
  await page.getByRole("option", { name: "Dynamic value" }).click();
  await expect(page.getByRole("combobox", { name: "Dynamic source" })).toHaveText("Reference date");
  await page.getByRole("button", { name: "Preview example values" }).click();
  await expect(page.getByText(/\/parameters\/0\/value: 2026-10-20/)).toBeVisible();
  await save(page);
  expect(getRevision().dynamicValues.bindings[0]).toMatchObject({ target: "/parameters/0/value", source: "ReferenceDate", format: "IsoDate" });
});

test("HTTP query-map fields offer dynamic reference dates", async ({ page }) => {
  const getRevision = await setup(page, http);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await page.getByRole("button", { name: "Add entry" }).click();
  await page.getByLabel("Parameter name").fill("referenceDate");
  await page.getByRole("combobox", { name: "Value source" }).click();
  await page.getByRole("option", { name: "Dynamic value" }).click();
  await page.getByRole("combobox", { name: "Dynamic source" }).click();
  await page.getByRole("option", { name: "Reference date" }).click();
  await page.getByRole("button", { name: "Preview example values" }).click();
  await expect(page.getByText(/\/query\/referenceDate: 2026-10-20/)).toBeVisible();
  await save(page);
  expect(getRevision().dynamicValues.bindings[0]).toMatchObject({ target: "/query/referenceDate", source: "ReferenceDate", format: "IsoDate" });
});
