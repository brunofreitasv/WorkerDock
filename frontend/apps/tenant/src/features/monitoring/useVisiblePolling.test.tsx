import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { useVisiblePolling } from "./useVisiblePolling";

afterEach(() => {
  vi.useRealTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
});

test("polls visible views every five seconds and pauses while hidden", async () => {
  vi.useFakeTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  const load = vi.fn().mockResolvedValue({ count: 1 });
  const { result } = renderHook(() => useVisiblePolling(load));
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(load).toHaveBeenCalledTimes(2);
  act(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
  expect(load).toHaveBeenCalledTimes(2);
  expect(result.current.updatedAt).not.toBeNull();
  act(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(3);
});

test("backs off failed refreshes and preserves the last successful timestamp", async () => {
  vi.useFakeTimers();
  const load = vi.fn().mockResolvedValueOnce({ count: 1 }).mockRejectedValue(new Error("offline"));
  const { result } = renderHook(() => useVisiblePolling(load));
  await act(async () => { await Promise.resolve(); });
  const firstUpdatedAt = result.current.updatedAt;
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(load).toHaveBeenCalledTimes(2);
  expect(result.current.updatedAt).toEqual(firstUpdatedAt);
  expect(result.current.error).toBe(true);
  expect(result.current.stale).toBe(true);
  await act(async () => { await vi.advanceTimersByTimeAsync(9999); });
  expect(load).toHaveBeenCalledTimes(2);
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(load).toHaveBeenCalledTimes(3);
});

test("does not schedule another poll when an in-flight request resolves after unmount", async () => {
  vi.useFakeTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  let resolveRequest!: (value: { count: number }) => void;
  const pending = new Promise<{ count: number }>(resolve => { resolveRequest = resolve; });
  const load = vi.fn(() => pending);
  const { unmount } = renderHook(() => useVisiblePolling(load));
  expect(load).toHaveBeenCalledTimes(1);

  unmount();
  await act(async () => { resolveRequest({ count: 1 }); await pending; });
  expect(vi.getTimerCount()).toBe(0);
  await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
  expect(load).toHaveBeenCalledTimes(1);
});
