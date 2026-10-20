import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { DynamicValuePreview } from "./DynamicValuePreview";

afterEach(() => vi.unstubAllGlobals());

test("stale_preview_is_discarded", async () => {
  const pending: ((value: Response) => void)[] = [];
  vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
    const path = String(input);
    if (path.endsWith("/api/antiforgery"))
      return Promise.resolve({ ok: true, headers: new Headers(), json: async () => ({ requestToken: "test" }) });
    return new Promise<Response>(resolve => pending.push(resolve));
  }));
  const first = { templateId: "one", templateVersion: "1", configuration: {}, dynamicValues: { resolverVersion: 1 as const, timeZoneId: "UTC", bindings: [] }, policy: {}, triggerKind: "Manual" as const, scheduledForUtc: null, sampleAtUtc: "2026-10-20T12:00:00Z", referenceDateOverride: null, jobTimeout: null };
  const user = userEvent.setup();
  const view = render(<DynamicValuePreview request={first} t={translations.en} />);
  await user.click(screen.getByRole("button", { name: translations.en.previewDynamicValues }));
  await vi.waitFor(() => expect(pending).toHaveLength(1));
  view.rerender(<DynamicValuePreview request={{ ...first, templateId: "two" }} t={translations.en} />);
  await user.click(screen.getByRole("button", { name: translations.en.previewDynamicValues }));
  await vi.waitFor(() => expect(pending).toHaveLength(2));
  pending[1]({ ok: true, status: 200, json: async () => ({ issues: [], values: { current: "new" }, isExample: true }) } as Response);
  await screen.findByText("current: new");
  pending[0]({ ok: true, status: 200, json: async () => ({ issues: [], values: { stale: "old" }, isExample: true }) } as Response);
  await vi.waitFor(() => expect(screen.queryByText("stale: old")).not.toBeInTheDocument());
});

test("a null preview request never sends a request", async () => {
  const fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  render(<DynamicValuePreview request={null} t={translations.en} />);
  expect(screen.getByRole("button", { name: translations.en.previewDynamicValues })).toBeDisabled();
  expect(fetchMock).not.toHaveBeenCalled();
});
