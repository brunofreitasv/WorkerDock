import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { useVisiblePolling } from "./useVisiblePolling";

afterEach(() => {
  vi.useRealTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
});

test("uses a 15-second successful refresh interval", async () => {
  vi.useFakeTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  const load = vi.fn().mockResolvedValue({ count: 1 });
  renderHook(() => useVisiblePolling(load, undefined, 15000));
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
  expect(load).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
  expect(load).toHaveBeenCalledTimes(2);
});

test("keeps the five-second default for consumers without a custom interval", async () => {
  vi.useFakeTimers();
  const load = vi.fn().mockResolvedValue({ count: 1 });
  renderHook(() => useVisiblePolling(load));
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(4999); });
  expect(load).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(load).toHaveBeenCalledTimes(2);
});

test("backs off from the configured interval and caps retries at one minute", async () => {
  vi.useFakeTimers();
  const load = vi.fn().mockRejectedValue(new Error("offline"));
  renderHook(() => useVisiblePolling(load, undefined, 15000));
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await vi.advanceTimersByTimeAsync(29999); });
  expect(load).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(load).toHaveBeenCalledTimes(2);
  await act(async () => { await vi.advanceTimersByTimeAsync(59999); });
  expect(load).toHaveBeenCalledTimes(2);
  await act(async () => { await vi.advanceTimersByTimeAsync(1); });
  expect(load).toHaveBeenCalledTimes(3);
  await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
  expect(load).toHaveBeenCalledTimes(4);
});

test("preserves stale data and its timestamp after a failed refresh", async () => {
  vi.useFakeTimers();
  const load = vi.fn().mockResolvedValueOnce({ count: 1 }).mockRejectedValue(new Error("offline"));
  const { result } = renderHook(() => useVisiblePolling(load, undefined, 15000));
  await act(async () => { await Promise.resolve(); });
  const firstUpdatedAt = result.current.updatedAt;
  await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
  expect(load).toHaveBeenCalledTimes(2);
  expect(result.current.data).toEqual({ count: 1 });
  expect(result.current.updatedAt).toEqual(firstUpdatedAt);
  expect(result.current.error).toBe(true);
  expect(result.current.stale).toBe(true);
});

test("pauses while hidden, refreshes on resume, and refreshes manually", async () => {
  vi.useFakeTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  const load = vi.fn().mockResolvedValue({ count: 1 });
  const { result } = renderHook(() => useVisiblePolling(load, undefined, 15000));
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(1);
  act(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
  expect(load).toHaveBeenCalledTimes(1);
  act(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(2);
  act(() => result.current.refresh());
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(3);
});

test("new reload keys fetch immediately and ignore a superseded response", async () => {
  vi.useFakeTimers();
  let resolveOld!: (value: string) => void;
  const oldRequest = new Promise<string>(resolve => { resolveOld = resolve; });
  const oldLoad = vi.fn(() => oldRequest);
  const newLoad = vi.fn().mockResolvedValue("new");
  const { result, rerender } = renderHook(({ key, load }) => useVisiblePolling(load, key, 15000), {
    initialProps: { key: "old", load: oldLoad },
  });
  expect(oldLoad).toHaveBeenCalledTimes(1);
  rerender({ key: "new", load: newLoad });
  await act(async () => { await Promise.resolve(); });
  expect(newLoad).toHaveBeenCalledTimes(1);
  expect(result.current.data).toBe("new");
  await act(async () => { resolveOld("old"); await oldRequest; });
  expect(result.current.data).toBe("new");
  expect(vi.getTimerCount()).toBe(1);
});

test("fifty visible views at fifteen seconds make 200 recurring requests per minute", async () => {
  vi.useFakeTimers();
  Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  const load = vi.fn().mockResolvedValue({ count: 1 });
  const hooks = Array.from({ length: 50 }, () => renderHook(() => useVisiblePolling(load, undefined, 15000)));
  await act(async () => { await Promise.resolve(); });
  expect(load).toHaveBeenCalledTimes(50);
  load.mockClear();
  await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
  expect(load).toHaveBeenCalledTimes(200);
  hooks.forEach(hook => hook.unmount());
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
