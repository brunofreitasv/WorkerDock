import { useEffect, useState } from "react";
import { Alert, Button, CircularProgress, Paper, Stack, Typography } from "@mui/material";
import { ApiError, mutate, read } from "../../api/client";
import type { Labels } from "../../../../../packages/ui/src/preferences";

interface VaultSettings { providerId: string; publicOptions: Record<string, never>; version: string }
export function VaultSettingsPage({ t }: { t: Labels }) {
  const [settings, setSettings] = useState<VaultSettings>();
  const [etag, setEtag] = useState<string>();
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  const [loading, setLoading] = useState(true);
  const load = () => { setError(""); setLoading(true); void read<VaultSettings>("/api/settings/vault").then(({ data, etag: tag }) => { setSettings(data); setEtag(tag ?? undefined); }).catch(() => setError(t.requestError)).finally(() => setLoading(false)); };
  useEffect(load, []);
  const save = async () => {
    if (!settings || !etag) return;
    setSaved(false);
    try { const next = await mutate<VaultSettings>("/api/settings/vault", "PUT", { providerId: settings.providerId, publicOptions: {} }, etag); setSettings(next); setEtag('"' + next.version + '"'); setSaved(true); setError(""); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); }
  };
  return <Paper component="section" aria-label={t.vaultSettings} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 760 }}><Stack spacing={2}>
    <Typography component="h2" variant="h6">{t.vaultSettings}</Typography>
    <Typography color="text.secondary">{t.vaultSettingsHelp}</Typography>
    {error && <Alert severity="error" action={<Button color="inherit" onClick={load}>{t.reload}</Button>}>{error}</Alert>}
    {saved && <Alert severity="success">{t.saved}</Alert>}
    {loading && !settings && <CircularProgress aria-label={t.loading} />}
    {settings && <><Typography><strong>{t.provider}</strong>: {settings.providerId}</Typography><Typography>{t.vaultReferenceOnly}</Typography><Button variant="contained" onClick={save} disabled={!etag} sx={{ alignSelf: { xs: "stretch", sm: "flex-start" } }}>{t.save}</Button></>}
  </Stack></Paper>;
}
