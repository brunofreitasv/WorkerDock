import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";

afterEach(() => vi.unstubAllGlobals());

test("saves dynamic bindings and sends a date-only Run now override", async () => {
  const jobId = "11111111-1111-4111-8111-111111111111";
  const descriptor = { id: "example", version: "1.1.0", displayName: "Example", description: "Example", configurationSchema: {
    type: "object", required: ["referenceDate"], properties: { referenceDate: { type: "string", "x-workerDock-dynamicValue": true } }, additionalProperties: false,
  } };
  let revision: any;
  let enabled = false;
  const runRequests: any[] = [];
  const previewRequests: any[] = [];
  const api = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    let status = 200;
    let body: any = {};
    if (url.pathname === "/api/templates") body = { items: [{ selectable: true, descriptor }] };
    else if (url.pathname === "/api/antiforgery") body = { requestToken: "test" };
    else if (url.pathname === "/api/jobs" && method === "POST") {
      revision = JSON.parse(String(init?.body)); status = 202; body = { targetId: jobId };
    } else if (url.pathname === `/api/jobs/${jobId}/configuration`) {
      body = { revision: { ...revision, revision: 1 }, version: [1], enabled, archived: false, concurrencyLimit: 1 };
    } else if (url.pathname === "/api/template-values/preview") {
      previewRequests.push(JSON.parse(String(init?.body)));
      body = { issues: [], values: { "/referenceDate": "2026-10-20" }, isExample: true };
    } else if (url.pathname === `/api/jobs/${jobId}/actions` && method === "POST") {
      enabled = true; status = 202; body = { targetId: jobId };
    } else if (url.pathname === `/api/jobs/${jobId}/runs` && method === "POST") {
      runRequests.push(JSON.parse(String(init?.body)));
      if (runRequests.length < 3) { status = 503; body = { code: "Unavailable" }; }
      else { status = 202; body = { targetId: "33333333-3333-4333-8333-333333333333" }; }
    }
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ETag: '"current"' } });
  });
  vi.stubGlobal("fetch", api);
  const user = userEvent.setup();
  render(<JobWizard t={translations.en} defaultZone="America/Sao_Paulo" />);
  await user.type(await screen.findByRole("textbox", { name: "Job name" }), "Dynamic date job");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("combobox", { name: "Value source" }));
  await user.click(await screen.findByRole("option", { name: "Dynamic value" }));
  await user.click(screen.getByRole("combobox", { name: "Dynamic source" }));
  await user.click(await screen.findByRole("option", { name: "Reference date" }));
  await user.click(screen.getByRole("button", { name: translations.en.previewDynamicValues }));
  await screen.findByText("/referenceDate: 2026-10-20");
  expect(previewRequests).toHaveLength(1);
  expect(previewRequests[0]).toMatchObject({ jobTimeout: null, dynamicValues: { bindings: expect.any(Array) } });
  expect(previewRequests[0]).not.toHaveProperty("policy");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  expect(screen.getByRole("switch", { name: translations.en.hideFailureDetails })).not.toBeChecked();
  await user.click(screen.getByRole("switch", { name: translations.en.hideFailureDetails }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Save disabled draft" }));
  await waitFor(() => expect(revision?.dynamicValues?.bindings).toHaveLength(1));
  expect(revision.hideFailureDetails).toBe(true);
  expect(revision.dynamicValues.bindings[0].source).toBe("ReferenceDate");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Enable job" }));
  await waitFor(() => expect(screen.getByText(/Enabled/)).toBeVisible());
  await user.click(screen.getByRole("button", { name: "Run now" }));
  await user.type(screen.getByLabelText("Reference date"), "2026-10-19");
  await user.click(screen.getAllByRole("button", { name: "Run now" }).at(-1)!);
  await waitFor(() => expect(runRequests).toHaveLength(1));
  await user.click(screen.getAllByRole("button", { name: "Run now" }).at(-1)!);
  await waitFor(() => expect(runRequests).toHaveLength(2));
  expect(runRequests[0].requestKey).toBe(runRequests[1].requestKey);
  await user.clear(screen.getByLabelText("Reference date"));
  await user.type(screen.getByLabelText("Reference date"), "2026-10-18");
  await user.click(screen.getAllByRole("button", { name: "Run now" }).at(-1)!);
  await waitFor(() => expect(runRequests).toHaveLength(3));
  expect(runRequests[2]).toMatchObject({ requestKey: expect.any(String), referenceDateOverride: "2026-10-18" });
  expect(runRequests[2].requestKey).not.toBe(runRequests[1].requestKey);
  expect(runRequests[0].referenceDateOverride).toBe("2026-10-19");
  expect(runRequests[0].referenceDateOverride).not.toContain("T");
}, 15_000);
