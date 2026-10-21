import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const mediaDir = path.resolve(scriptDir, "../apps/tenant/src/help/media");
const baseUrl = process.env.DOCS_CAPTURE_BASE_URL ?? "http://127.0.0.1:4173";
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1 });
const ids = {
  job: "11111111-1111-4111-8111-111111111111",
  execution: "22222222-2222-4222-8222-222222222222",
  sink: "33333333-3333-4333-8333-333333333333",
};

await mkdir(mediaDir, { recursive: true });
await page.route("**/api/session", route => route.fulfill({ json: { authenticated: true, canRead: true, canManage: true, mode: "RoleProtected", language: "en", timeZone: "UTC" } }));
await page.route("**/api/antiforgery", route => route.fulfill({ json: { requestToken: "synthetic-csrf" } }));
await page.route("**/api/templates", route => route.fulfill({ json: { items: [{ selectable: true, descriptor: { id: "sample-report", version: "1.1.0", displayName: "Sample report", description: "Synthetic documentation example", configurationSchema: { type: "object", additionalProperties: false, properties: {} } } }] } }));
await page.route("**/api/time-zones", route => route.fulfill({ json: ["America/Sao_Paulo", "UTC"] }));
await page.route("**/api/dashboard", route => route.fulfill({ json: { instanceId: ids.job, observedUtc: "2026-01-15T12:00:00Z", overall: "Healthy", queued: 0, running: 0, needsReview: 0, succeeded24h: 1, failed24h: 0, longRunning: 0, upcoming: 0, readyWorkers: 1, totalWorkers: 1, desiredCapacityRevision: 1, effectiveCapacityRevision: 1, capacityState: "Applied", pendingProjections: 0, upcomingItems: [], recentItems: [{ executionId: ids.execution, jobId: ids.job, jobName: "guide-demo", state: "Succeeded", createdAtUtc: "2026-01-15T11:55:00Z", correlationId: "synthetic-correlation" }] } }));
await page.route(`**/api/monitoring/executions/${ids.execution}`, route => route.fulfill({ json: { executionId: ids.execution, jobId: ids.job, jobName: "guide-demo", templateId: "sample-report", state: "Succeeded", certainty: "Certain", triggerKind: "Manual", createdAtUtc: "2026-01-15T11:55:00Z", availableAtUtc: "2026-01-15T11:55:00Z", scheduledForUtc: null, correlationId: "synthetic-correlation", stopRequested: false, forceStop: false, attempts: [{ attemptId: "synthetic-attempt", attemptNumber: 1, workerId: "synthetic-worker", state: "Succeeded", startedAtUtc: "2026-01-15T11:55:01Z", endedAtUtc: "2026-01-15T11:55:03Z", deadlineUtc: null, safeCode: null, localExitConfirmed: true, effectsMayHaveOccurred: false }], timeline: [{ eventId: "synthetic-event-1", attemptId: "synthetic-attempt", state: "Queued", occurredAtUtc: "2026-01-15T11:55:00Z" }, { eventId: "synthetic-event-2", attemptId: "synthetic-attempt", state: "Running", occurredAtUtc: "2026-01-15T11:55:01Z" }, { eventId: "synthetic-event-3", attemptId: "synthetic-attempt", state: "Succeeded", occurredAtUtc: "2026-01-15T11:55:03Z" }] } }));
await page.route("**/api/alert-rules", route => route.fulfill({ json: [] }));
await page.route("**/api/alert-sinks", route => route.fulfill({ json: [{ id: ids.sink, name: "Synthetic operations sink", enabled: true, url: "https://example.test/alerts", version: "synthetic-v1" }] }));
await page.route("**/api/alerts/incidents**", route => route.fulfill({ json: [] }));
await page.route("**/api/alerts/deliveries**", route => route.fulfill({ json: [] }));

async function annotate(locator, number, label) {
  await locator.evaluate((element, annotation) => {
    const rect = element.getBoundingClientRect();
    const badge = document.createElement("div");
    badge.dataset.documentationAnnotation = "true";
    badge.textContent = `${annotation.number} ${annotation.label}`;
    Object.assign(badge.style, { position: "fixed", left: `${Math.max(8, rect.left)}px`, top: `${Math.max(8, rect.top)}px`, zIndex: "2147483647", padding: "5px 9px", borderRadius: "14px", color: "white", background: "#17243a", font: "600 13px system-ui", boxShadow: "0 2px 8px #0005", pointerEvents: "none" });
    document.body.append(badge);
  }, { number, label });
}

async function clearAnnotations() {
  await page.evaluate(() => document.querySelectorAll("[data-documentation-annotation]").forEach(element => element.remove()));
}

try {
  await page.goto(`${baseUrl}/#/jobs`);
  await page.getByRole("heading", { name: "Create job" }).waitFor();
  await annotate(page.getByRole("heading", { name: "Create job" }), 1, "Choose template");
  await page.screenshot({ path: path.join(mediaDir, "wizard-job.png"), fullPage: true });

  await clearAnnotations();
  await page.goto(`${baseUrl}/#/dashboard`);
  await page.getByRole("button", { name: /guide-demo/ }).waitFor();
  await page.getByRole("button", { name: /guide-demo/ }).click();
  await page.getByRole("heading", { name: "Timeline" }).waitFor();
  await annotate(page.getByRole("heading", { name: "Timeline" }), 2, "Compare recorded states");
  await page.screenshot({ path: path.join(mediaDir, "execution-timeline.png"), fullPage: true });

  await clearAnnotations();
  await page.goto(`${baseUrl}/#/alerts`);
  await page.getByRole("button", { name: "New alert rule" }).waitFor();
  await page.getByRole("button", { name: "New alert rule" }).click();
  const nameField = page.getByRole("textbox", { name: "Rule name" });
  await nameField.waitFor();
  await nameField.evaluate(element => element.scrollIntoView({ block: "center" }));
  await page.setViewportSize({ width: 1440, height: 1200 });
  await annotate(nameField, 3, "Name the disabled rule");
  await page.screenshot({ path: path.join(mediaDir, "alert-rule.png") });
  console.log(`Captured three synthetic documentation screenshots in ${mediaDir}`);
} finally {
  await browser.close();
}
