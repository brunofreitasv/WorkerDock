import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, expect, test, vi } from "vitest";
import { translations, type Language } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";

afterEach(() => vi.unstubAllGlobals());

test("changing wizard language preserves the draft and selected local instant, offset, and preview UTC", async () => {
  const descriptor = { id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } };
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input);
    const body = path.endsWith("/api/templates") ? { items: [{ selectable: true, descriptor }] }
      : path.endsWith("/api/time-zones") ? ["America/New_York", "UTC"]
        : path.endsWith("/api/schedules/preview") ? [{ utc: "2026-11-01T06:30:00Z", offset: "-05:00" }]
          : path.endsWith("/api/antiforgery") ? { requestToken: "test" } : {};
    return { ok: true, status: 200, headers: new Headers(), json: async () => body };
  }));
  function Harness() {
    const [language, setLanguage] = useState<Language>("en");
    return <JobWizard t={translations[language]} language={language} setLanguage={setLanguage} />;
  }
  const user = userEvent.setup();
  render(<Harness />);
  await user.type(await screen.findByRole("textbox"), "Draft intact");
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("button", { name: "Next" }));
  await user.click(screen.getByRole("tab", { name: "Schedule" }));
  await user.click(screen.getByLabelText("Use a schedule"));
  await user.click(screen.getByRole("combobox", { name: "Schedule kind" }));
  await user.click(await screen.findByRole("option", { name: "One time" }));
  await user.click(screen.getByRole("combobox", { name: "Time zone" }));
  await user.click(await screen.findByRole("option", { name: "America/New_York" }));
  await user.type(screen.getByLabelText("Local date and time"), "2026-11-01T01:30");
  await user.type(screen.getByLabelText("UTC offset"), "-05:00");
  await user.click(screen.getByRole("button", { name: "Preview next runs" }));
  await waitFor(() => expect(screen.getByTestId("resolved-utc")).toHaveValue("2026-11-01T06:30:00Z"));

  await user.click(screen.getByRole("combobox", { name: "Wizard language" }));
  await user.click(await screen.findByRole("option", { name: "Português (Brasil)" }));

  expect(screen.getByDisplayValue("2026-11-01T01:30")).toBeVisible();
  expect(screen.getByDisplayValue("-05:00")).toBeVisible();
  expect(screen.getByTestId("resolved-utc")).toHaveValue("2026-11-01T06:30:00Z");
  await user.click(screen.getByRole("tab", { name: translations["pt-BR"].stageTemplate }));
  expect(screen.getByDisplayValue("Draft intact")).toBeVisible();
});
