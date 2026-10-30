import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { translations, type TemplateDescriptorDto } from "@workerdock/ui";
import { ManagedJobDetail } from "./ManagedJobDetail";
import { ManagedJobEditor } from "./ManagedJobEditor";

vi.mock("@workerdock/ui", async importOriginal => ({ ...(await importOriginal<typeof import("@workerdock/ui")>()), TemplateForm: () => <div data-testid="template-form" /> }));
afterEach(() => vi.unstubAllGlobals());

test("read-only job detail groups its execution controls", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ limit: 2, pauseOnNeedsReview: true }), { status: 200, headers: { "Content-Type": "application/json" } })));
  render(<ManagedJobDetail jobId="job-1" returnRoute="jobs" t={translations.en} canManage={false} onNavigate={() => {}} />);
  const section = await screen.findByRole("region", { name: translations.en.jobSectionOverview });
  await waitFor(() => expect(section).toContainElement(screen.getByRole("spinbutton", { name: "Job capacity" })));
  expect(screen.queryByRole("button", { name: translations.en.jobEditAction })).not.toBeInTheDocument();
});

test("job editor groups configuration, schedule, policy, and actions", () => {
  render(<ManagedJobEditor jobId="job-1" initial={{ revision: { name: "Job" }, schedule: null, missedPolicy: null }} etag={'"1"'} descriptor={{} as TemplateDescriptorDto} t={translations.en} onSaved={() => {}} onCancel={() => {}} />);
  for (const label of [translations.en.jobSectionConfiguration, translations.en.jobSectionSchedule, translations.en.jobSectionPolicy]) {
    expect(screen.getByRole("region", { name: label })).toBeInTheDocument();
  }
  expect(screen.getByRole("group", { name: translations.en.jobReviewSave })).toContainElement(screen.getByRole("button", { name: translations.en.jobReviewSave }));
});
