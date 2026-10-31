import { useCallback, useEffect, useRef, useState } from "react";

export interface VisiblePollingState<T> {
  data: T | undefined;
  loading: boolean;
  error: boolean;
  stale: boolean;
  updatedAt: Date | null;
  refresh(): void;
}

const BASE_DELAY_MS = 5000;
const MAX_DELAY_MS = 60000;

export function useVisiblePolling<T>(load: () => Promise<T>, reloadKey?: string, intervalMs = BASE_DELAY_MS): VisiblePollingState<T> {
  const loadRef = useRef(load);
  const generation = useRef(0);
  const inFlightGeneration = useRef<number | null>(null);
  const active = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const delay = useRef(intervalMs);
  const [data, setData] = useState<T>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  loadRef.current = load;

  const poll = useCallback(async () => {
    const requestGeneration = generation.current;
    if (inFlightGeneration.current === requestGeneration || document.visibilityState !== "visible") return;
    inFlightGeneration.current = requestGeneration;
    try {
      const result = await loadRef.current();
      if (active.current && generation.current === requestGeneration) {
        setData(result);
        setUpdatedAt(new Date());
        setError(false);
        delay.current = intervalMs;
      }
    } catch {
      if (active.current && generation.current === requestGeneration) {
        setError(true);
        delay.current = Math.min(delay.current * 2, MAX_DELAY_MS);
      }
    } finally {
      if (inFlightGeneration.current === requestGeneration) inFlightGeneration.current = null;
      if (active.current && generation.current === requestGeneration) {
        setLoading(false);
        if (document.visibilityState === "visible") timer.current = setTimeout(() => void poll(), delay.current);
      }
    }
  }, [intervalMs]);

  useEffect(() => {
    generation.current += 1;
    const effectGeneration = generation.current;
    active.current = true;
    delay.current = intervalMs;
    const stop = () => { if (timer.current !== null) clearTimeout(timer.current); timer.current = null; };
    const visibilityChanged = () => {
      stop();
      if (document.visibilityState === "visible") void poll();
    };
    document.addEventListener("visibilitychange", visibilityChanged);
    if (document.visibilityState === "visible") void poll();
    return () => { if (generation.current === effectGeneration) active.current = false; stop(); document.removeEventListener("visibilitychange", visibilityChanged); };
  }, [poll, reloadKey, intervalMs]);

  const refresh = useCallback(() => { if (document.visibilityState === "visible") { if (timer.current !== null) clearTimeout(timer.current); timer.current = null; void poll(); } }, [poll]);
  return { data, loading, error, stale: error && data !== undefined, updatedAt, refresh };
}
