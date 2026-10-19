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

export function useVisiblePolling<T>(load: () => Promise<T>, reloadKey?: string): VisiblePollingState<T> {
  const loadRef = useRef(load);
  const inFlight = useRef(false);
  const active = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const delay = useRef(BASE_DELAY_MS);
  const [data, setData] = useState<T>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  loadRef.current = load;

  const poll = useCallback(async () => {
    if (inFlight.current || document.visibilityState !== "visible") return;
    inFlight.current = true;
    try {
      const result = await loadRef.current();
      setData(result);
      setUpdatedAt(new Date());
      setError(false);
      delay.current = BASE_DELAY_MS;
    } catch {
      setError(true);
      delay.current = Math.min(delay.current * 2, MAX_DELAY_MS);
    } finally {
      inFlight.current = false;
      setLoading(false);
      if (active.current && document.visibilityState === "visible") timer.current = setTimeout(() => void poll(), delay.current);
    }
  }, []);

  useEffect(() => {
    active.current = true;
    const stop = () => { if (timer.current !== null) clearTimeout(timer.current); timer.current = null; };
    const visibilityChanged = () => {
      stop();
      if (document.visibilityState === "visible") void poll();
    };
    document.addEventListener("visibilitychange", visibilityChanged);
    if (document.visibilityState === "visible") void poll();
    return () => { active.current = false; stop(); document.removeEventListener("visibilitychange", visibilityChanged); };
  }, [poll, reloadKey]);

  const refresh = useCallback(() => { if (document.visibilityState === "visible") { if (timer.current !== null) clearTimeout(timer.current); timer.current = null; void poll(); } }, [poll]);
  return { data, loading, error, stale: error && data !== undefined, updatedAt, refresh };
}
