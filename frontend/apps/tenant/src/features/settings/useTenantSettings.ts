import { useCallback, useEffect, useState } from "react";
import { read } from "../../api/client";

export interface TenantSettings {
  displayName: string;
  displayTimeZone: string;
  version: string;
}

export interface TenantSettingsSnapshot {
  data: TenantSettings;
  etag: string | null;
}

export function useTenantSettings(enabled: boolean) {
  const [current, setCurrent] = useState<TenantSettingsSnapshot>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    if (!enabled) return;
    let alive = true;
    setLoading(true);
    setError(false);
    void read<TenantSettings>("/api/settings")
      .then((result) => {
        if (alive) setCurrent({ data: result.data, etag: result.etag });
      })
      .catch(() => {
        if (alive) setError(true);
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => { alive = false; };
  }, [enabled, reloadKey]);

  const reload = useCallback(() => setReloadKey((value) => value + 1), []);
  const acceptSave = useCallback((data: TenantSettings) => {
    setCurrent({ data, etag: `"${data.version}"` });
    setError(false);
  }, []);

  return { current, loading, error, reload, acceptSave };
}
