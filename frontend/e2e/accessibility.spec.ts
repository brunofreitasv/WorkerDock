import { expect, test } from "@playwright/test";

const dashboard = {
  instanceId: "11111111-1111-4111-8111-111111111111",
  observedUtc: "2026-10-18T12:00:00Z",
  overall: "Degraded",
  queued: 0,
  running: 0,
  needsReview: 1,
  succeeded24h: 2,
  failed24h: 3,
  longRunning: 0,
  upcoming: 0,
  readyWorkers: 0,
  totalWorkers: 1,
  desiredCapacityRevision: 2,
  effectiveCapacityRevision: 2,
  capacityState: "Applied",
  upcomingItems: [],
  recentItems: [],
};

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.clear());
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: false, canRead: true, canManage: false, mode: "Anonymous", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/dashboard", route => route.fulfill({ json: dashboard }));
  await page.route("**/api/monitoring/jobs**", route => route.fulfill({ json: { items: [], nextCursor: null } }));
});

test("dashboard supports keyboard navigation and shows health without color cues", async ({ page }) => {
  await page.goto("/#/dashboard");

  await expect(page.getByRole("main")).toBeVisible();
  await expect(page.getByRole("heading", { level: 1, name: "Dashboard" })).toBeVisible();
  await expect(page.getByText("Degraded", { exact: true })).toBeVisible();
  await expect(page.getByText("Failed 24h", { exact: true })).toBeVisible();

  const monitoring = page.getByRole("link", { name: "Monitoring" });
  await monitoring.focus();
  await expect(monitoring).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("table", { name: "Jobs" })).toBeVisible({ timeout: 15_000 });
});

test("dashboard remains usable on mobile and switches accessible labels to Brazilian Portuguese", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/#/dashboard");
  await expect(page.getByRole("main")).toBeVisible();

  const language = page.getByRole("combobox", { name: "Language" });
  await language.selectOption("pt-BR");

  await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
  await expect(page.getByText("Estado:", { exact: false })).toBeVisible();
  await expect(page.getByText("Nenhuma execução recente.")).toBeVisible();
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});

test("job creation is keyboard operable with visible focus and localized validation", async ({ page }) => {
  await page.unroute("**/api/session");
  await page.route("**/api/session", route => route.fulfill({ json: {
    authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC",
  } }));
  await page.route("**/api/templates", route => route.fulfill({ json: { items: [{
    selectable: true,
    descriptor: { id: "sample", version: "1", displayName: "Sample", description: "Safe sample", configurationSchema: { type: "object", properties: {} } },
  }] } }));

  for (const locale of ["en", "pt-BR"] as const) {
    await page.goto("/#/jobs");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    const language = page.getByRole("combobox", { name: "Wizard language" });
    if (locale === "pt-BR") {
      await language.focus();
      await page.keyboard.press("Enter");
      await page.keyboard.press("ArrowDown");
      await page.keyboard.press("Enter");
    }

    const jobName = page.getByRole("textbox", { name: locale === "en" ? "Job name" : "Nome da tarefa" });
    await jobName.focus();
    await page.keyboard.press("Tab");
    const template = page.getByRole("combobox", { name: locale === "en" ? "Template" : "Modelo" });
    await expect(template).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("listbox")).toBeVisible();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    await page.keyboard.press("Tab");
    const next = page.getByRole("button", { name: locale === "en" ? "Next" : "Avançar" });
    await expect(next).toBeFocused();
    const focusIsVisible = await next.evaluate(element => element.matches(":focus-visible"));
    expect(focusIsVisible).toBe(true);
    await page.keyboard.press("Enter");

    const alert = page.getByRole("alert");
    await expect(alert).toBeVisible();
    await expect(alert).toHaveText(locale === "en"
      ? "Choose a template and enter a job name."
      : "Escolha um modelo e informe o nome da tarefa.");
    await expect(alert).toBeFocused();
  }
});
