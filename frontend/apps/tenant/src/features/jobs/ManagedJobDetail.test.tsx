import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { translations } from "@workerdock/ui";
import { ManagedJobDetail } from "./ManagedJobDetail";

afterEach(() => vi.unstubAllGlobals());

test("viewers load only execution controls and receive no management actions", async () => {
  const paths: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), "http://localhost");
    paths.push(url.pathname);
    return new Response(JSON.stringify({ limit: 2, pauseOnNeedsReview: true }), { status: 200, headers: { "Content-Type": "application/json" } });
  }));
  render(<ManagedJobDetail jobId="job-1" returnRoute="jobs" t={translations.en} canManage={false} onNavigate={() => {}} />);
  await waitFor(() => expect(screen.getByRole("spinbutton", { name: "Job capacity" })).toHaveValue(2));
  expect(screen.getByRole("switch", { name: "Hold new starts while review is pending" })).toBeChecked();
  expect(screen.getByRole("spinbutton", { name: "Job capacity" })).toBeDisabled();
  expect(screen.getByRole("switch")).toBeDisabled();
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: translations.en.jobEditAction })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: translations.en.runNow })).not.toBeInTheDocument();
  expect(paths).toEqual(["/api/jobs/job-1/execution-controls"]);
});
