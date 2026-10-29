import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { useTenantSettings } from "./useTenantSettings";

afterEach(() => vi.unstubAllGlobals());

test("loads settings for readers and publishes saved settings with a new ETag", async () => {
  const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
    new Response(JSON.stringify({ displayName: init?.method ? "Equipe" : "Team", displayTimeZone: "UTC", version: init?.method ? "v2" : "v1" }), {
      status: 200,
      headers: { ETag: '"v1"', "Content-Type": "application/json" },
    }),
  );
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() => useTenantSettings(true));
  await waitFor(() => expect(result.current.current?.data.displayName).toBe("Team"));
  expect(result.current.current?.etag).toBe('"v1"');
  result.current.acceptSave({ displayName: "Equipe", displayTimeZone: "UTC", version: "v2" });
  await waitFor(() => expect(result.current.current).toEqual({
      data: { displayName: "Equipe", displayTimeZone: "UTC", version: "v2" },
      etag: '"v2"',
    }));
});

test("does not request settings until read access is enabled", async () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  const { rerender } = renderHook(({ enabled }) => useTenantSettings(enabled), { initialProps: { enabled: false } });
  expect(fetch).not.toHaveBeenCalled();
  rerender({ enabled: true });
  await waitFor(() => expect(fetch).toHaveBeenCalledWith("/api/settings", expect.anything()));
});

test("Tenant_name_updates_after_reload", async () => {
  let version = 0;
  vi.stubGlobal("fetch", vi.fn(async () => {
    version++;
    return new Response(JSON.stringify({ displayName: version === 1 ? "Team" : "Persisted team name", displayTimeZone: "UTC", version: `v${version}` }), {
      status: 200,
      headers: { ETag: `"v${version}"`, "Content-Type": "application/json" },
    });
  }));
  const { result } = renderHook(() => useTenantSettings(true));
  await waitFor(() => expect(result.current.current?.data.displayName).toBe("Team"));
  result.current.reload();
  await waitFor(() => expect(result.current.current?.data.displayName).toBe("Persisted team name"));
});
