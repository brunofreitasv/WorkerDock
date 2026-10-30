import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { translations } from "@workerdock/ui";
import { JobInventory } from "./JobInventory";

const page = { items: [{ jobId: "abc", name: "Café_100%", templateId: "demo", templateVersion: "1", enabled: false, archived: false, scheduleKind: null, scheduleZoneId: null, schedulePaused: false, configurationUpdatedUtc: "2020-01-01T00:00:00Z" }], nextCursor: null };

describe("JobInventory", () => {
  it("loads and searches old persisted jobs without storing configuration", async () => {
    const user = userEvent.setup();
    const onNavigate = vi.fn();
    const fetcher = vi.fn().mockResolvedValue({ ok: true, json: async () => page, headers: { get: () => null } });
    vi.stubGlobal("fetch", fetcher);
    render(<JobInventory t={translations.en} route="jobs" canManage onNavigate={onNavigate} />);
    expect(await screen.findByText("Café_100%")).toBeInTheDocument();
    await user.type(screen.getByLabelText(/search jobs/i), "Café_100%");
    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith("jobs?view=current&search=Caf%C3%A9_100%25"));
    expect(window.localStorage.length).toBe(0);
    vi.unstubAllGlobals();
  });

  it("distinguishes an unavailable inventory from an empty one", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("offline")));
    render(<JobInventory t={translations.en} route="jobs" canManage onNavigate={vi.fn()} />);
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/no jobs yet/i)).not.toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("lets viewers browse and open jobs without management controls", async () => {
    const user = userEvent.setup();
    const onNavigate = vi.fn();
    const paths: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      paths.push(new URL(String(input), "http://localhost").pathname);
      return { ok: true, json: async () => page, headers: { get: () => null } };
    }));
    render(<JobInventory t={translations.en} route="jobs" canManage={false} onNavigate={onNavigate} />);
    await user.click(await screen.findByRole("link", { name: "Café_100%" }));
    expect(paths).toEqual(["/api/jobs"]);
    expect(onNavigate).toHaveBeenCalledWith("jobs/abc");
    expect(screen.queryByRole("button", { name: translations.en.createJob })).not.toBeInTheDocument();
    expect(screen.queryByText(translations.en.permission)).not.toBeInTheDocument();
  });
});
