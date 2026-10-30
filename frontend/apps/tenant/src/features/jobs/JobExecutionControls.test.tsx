import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { JobExecutionControls } from "./JobExecutionControls";

test("shows stored capacity and review behavior without mutation controls to read-only users", () => {
  render(<JobExecutionControls jobId="job-1" limit={3} pauseOnNeedsReview overlap="Queue" canManage={false} t={translations.en} onSaved={() => {}} />);
  expect(screen.getByRole("spinbutton", { name: "Job capacity" })).toHaveValue(3);
  expect(screen.getByRole("switch", { name: "Pause this job until you review an unresolved run" })).toBeChecked();
  expect(screen.getByRole("switch")).toBeDisabled();
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
});

test("uses the skip-specific review label for Skip", () => {
  render(<JobExecutionControls jobId="job-1" limit={1} pauseOnNeedsReview={false} overlap="Skip" canManage t={translations.en} onSaved={() => {}} />);
  expect(screen.getByRole("switch", { name: "Skip scheduled runs until you review an unresolved run" })).toBeInTheDocument();
});

test("explains zero capacity and hides the review switch for Allow", async () => {
  const user = userEvent.setup();
  render(<JobExecutionControls jobId="job-1" limit={0} pauseOnNeedsReview overlap="Allow" canManage t={translations.en} onSaved={() => {}} />);
  expect(screen.queryByRole("switch")).not.toBeInTheDocument();
  expect(screen.getByText(/Allow can run overlapping runs up to job and host capacity/)).toBeVisible();
  expect(screen.getByText(/Zero pauses new starts/)).toBeVisible();
  await user.clear(screen.getByRole("spinbutton", { name: "Job capacity" }));
  await user.type(screen.getByRole("spinbutton", { name: "Job capacity" }), "1");
  fireEvent.blur(screen.getByRole("spinbutton", { name: "Job capacity" }));
  expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
});

test("saves limit and review behavior in one request", async () => {
  const onSaved = vi.fn();
  const requests: Array<{ url: string; body: unknown; method?: string }> = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), "http://localhost");
    const method = init?.method ?? "GET";
    if (url.pathname === "/api/antiforgery") return new Response(JSON.stringify({ requestToken: "test" }), { status: 200 });
    requests.push({ url: url.pathname, body: init?.body ? JSON.parse(String(init.body)) : undefined, method });
    return new Response(JSON.stringify({ targetId: "job-1" }), { status: 202 });
  }));
  const user = userEvent.setup();
  render(<JobExecutionControls jobId="job-1" limit={1} pauseOnNeedsReview={false} overlap="Queue" canManage t={translations.en} onSaved={onSaved} />);
  await user.clear(screen.getByRole("spinbutton", { name: "Job capacity" }));
  await user.type(screen.getByRole("spinbutton", { name: "Job capacity" }), "4");
  await user.click(screen.getByRole("switch", { name: "Pause this job until you review an unresolved run" }));
  await user.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => expect(onSaved).toHaveBeenCalledOnce());
  expect(requests).toContainEqual({ url: "/api/jobs/job-1/execution-controls", method: "PUT", body: { limit: 4, pauseOnNeedsReview: true } });
});
