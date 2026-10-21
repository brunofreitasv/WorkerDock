import { expect, test } from "@playwright/test";

test.describe.configure({ mode: "serial" });

async function recordScrollBehavior(page: import("@playwright/test").Page) {
  await page.evaluate(() => {
    const scrollIntoView = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function (options?: ScrollIntoViewOptions) {
      document.documentElement.dataset.lastScrollBehavior = typeof options === "object" ? options.behavior ?? "auto" : "auto";
      document.documentElement.dataset.lastScrollTarget = this.id;
      return scrollIntoView.call(this, options);
    };
  });
}

async function openApp(page: import("@playwright/test").Page, hash: string, canManage = false) {
  const documentationRequests: string[] = [];
  page.on("request", request => {
    const url = new URL(request.url());
  if (/\.md(?:\?|$)/i.test(url.href) || (/documentation|\/help/i.test(url.pathname) && url.protocol.startsWith("http") && !["127.0.0.1", "localhost"].includes(url.hostname))) {
      documentationRequests.push(url.href);
    }
  });
  await page.route("**/api/**", route => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname === "/api/session") {
      return route.fulfill({ json: {
        authenticated: false,
        canRead: true,
        canManage,
        mode: "Anonymous",
        language: "en",
        timeZone: "UTC",
      } });
    }
    if (["/api/alerts/incidents", "/api/alerts/deliveries", "/api/alert-rules", "/api/alert-sinks"].includes(pathname)) {
      return route.fulfill({ json: [] });
    }
    return route.fulfill({ json: {} });
  });
  await page.goto(`/${hash}`, { waitUntil: "domcontentloaded" });
  return documentationRequests;
}

test("English readers can search, follow guides, use the outline, and recover from no results", async ({ page }) => {
  const documentationRequests = await openApp(page, "#/help");
  await expect(page.getByRole("heading", { level: 1, name: "Documentation" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Common tasks" })).toBeVisible();
  await page.screenshot({ path: "test-results/documentation-desktop.png", fullPage: true });

  const search = page.getByRole("searchbox", { name: "Search documentation" });
  await search.fill("dashboard");
  await expect(page.getByRole("region", { name: "Search results" }).getByRole("link").first()).toHaveText("Understand the dashboard");
  await search.fill("no-such-documentation-topic");
  await expect(page.getByText("No documentation matched your search.")).toBeVisible();
  await page.getByRole("button", { name: "Clear search" }).click();
  await expect(page.getByRole("heading", { name: "Common tasks" })).toBeVisible();

  await page.goto("/#/help:monitoring");
  await expect(page.getByRole("heading", { name: "Monitor executions" })).toBeVisible();
  await page.emulateMedia({ reducedMotion: "reduce" });
  await recordScrollBehavior(page);
  await page.getByRole("link", { name: "Operate and monitor" }).click();
  await expect(page).toHaveURL(/#\/help:monitoring$/);
  await expect(page.locator("#topic-rail")).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-last-scroll-behavior", "auto");
  await expect(page.locator("html")).toHaveAttribute("data-last-scroll-target", "topic-rail");
  await expect(page.getByRole("heading", { name: "Monitor executions" })).toBeVisible();
  const outline = page.getByRole("navigation", { name: "On this page" });
  const firstOutlineLink = outline.getByRole("link").first();
  await expect(firstOutlineLink).toBeVisible();
  const targetId = await firstOutlineLink.getAttribute("href");
  await firstOutlineLink.click();
  await expect(page.locator(targetId!)).toBeVisible();
  await expect(page).toHaveURL(/#\/help:monitoring$/);
  await expect(page.getByRole("heading", { name: "Monitor executions" })).toBeVisible();

  await page.goto("/#/help:workers-audit");
  await expect(page.getByRole("heading", { name: "Check worker health" })).toBeVisible();
  await page.goto("/#/help:alerts");
  await expect(page.getByRole("heading", { name: "Create alert rules" })).toBeVisible();

  await page.goto("/#/dashboard");
  await page.getByRole("button", { name: "Documentation for this page" }).click();
  await expect(page).toHaveURL(/#\/help:dashboard$/);
  await expect(page.getByRole("heading", { name: "Understand the dashboard" })).toBeVisible();
  await page.getByRole("link", { name: "Alerts", exact: true }).click();
  await expect(page).toHaveURL(/#\/alerts$/);
  await expect(page.getByRole("heading", { level: 1, name: "Alerts" })).toBeVisible();
  await page.getByRole("button", { name: "Documentation for this page" }).click();
  await expect(page).toHaveURL(/#\/help:alert-rules$/);
  await expect(page.getByRole("heading", { name: "Create alert rules" })).toBeVisible();
  expect(documentationRequests).toEqual([]);
});

test("readers see read guides without management actions", async ({ page }) => {
  await openApp(page, "#/help:dashboard");
  await expect(page.getByRole("button", { name: "Open Understand the dashboard" })).toBeVisible();
  await page.goto("/#/help:alert-rules");
  await expect(page.getByRole("heading", { name: "Create alert rules" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open Create alert rules" })).toHaveCount(0);
});

test("Portuguese deep links, keyboard navigation, and long labels fit a narrow viewport", async ({ page }) => {
  const documentationRequests = await openApp(page, "#/help");
  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto("/#/help:settings");
  await expect(page.getByRole("heading", { name: "Manage tenant settings" })).toBeVisible();
  const language = page.getByRole("combobox", { name: "Language" });
  await language.selectOption("pt-BR");
  await expect(page).toHaveURL(/#\/help:settings$/);
  await expect(page.getByRole("heading", { name: "Gerencie as configurações do tenant" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await recordScrollBehavior(page);
  await page.getByRole("link", { name: "Administração" }).last().click();
  const topicDetails = page.locator("details#mobile-topic-rail");
  await expect(topicDetails).toHaveAttribute("open", "");
  await expect(topicDetails.getByRole("navigation", { name: "Explorar tópicos" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-last-scroll-behavior", "auto");
  await expect(page.locator("html")).toHaveAttribute("data-last-scroll-target", "mobile-topic-rail");
  await page.getByRole("link", { name: "Documentação" }).last().click();
  await expect(page.getByRole("heading", { level: 1, name: "Documentação" })).toBeVisible();
  await page.goto("/#/help:settings");
  await page.locator("summary").filter({ hasText: "Nesta página" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("navigation", { name: "Nesta página" })).toBeVisible();
  await page.locator("summary").filter({ hasText: "Explorar tópicos" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("navigation", { name: "Explorar tópicos" })).toBeVisible();

  const firstTopic = page.getByRole("navigation", { name: "Explorar tópicos" }).getByRole("link").first();
  await page.keyboard.press("Tab");
  await expect(firstTopic).toBeFocused();
  await page.screenshot({ path: "test-results/documentation-narrow.png", fullPage: true });
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/help:settings$/);
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
  expect(documentationRequests).toEqual([]);
});
