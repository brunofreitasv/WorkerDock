import { useEffect, useState, type FormEvent } from "react";
import { Alert, Box, Button, CircularProgress, Paper, Stack, TextField, Typography } from "@mui/material";
import { ApiError, mutate } from "../../api/client";
import { timeZones, type Labels } from "@workerdock/ui";
import type { TenantSettings, TenantSettingsSnapshot } from "./useTenantSettings";

interface Props {
  t: Labels;
  current?: TenantSettingsSnapshot;
  loading: boolean;
  error: boolean;
  reload: () => void;
  acceptSave: (data: TenantSettings) => void;
}

export function TenantSettingsForm({ t, current, loading, error: loadError, reload, acceptSave }: Props) {
  const [name, setName] = useState("");
  const [zone, setZone] = useState("UTC");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  const [invalid, setInvalid] = useState(false);

  useEffect(() => {
    if (current) {
      setName(current.data.displayName);
      setZone(current.data.displayTimeZone);
    }
  }, [current]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    setInvalid(true);
    if (!name.trim() || name.length > 200 || !timeZones.includes(zone) || !current?.etag) return;
    setBusy(true);
    setError("");
    try {
      const updated = await mutate<TenantSettings>("/api/settings", "PUT", { displayName: name, displayTimeZone: zone }, current.etag);
      acceptSave(updated);
      setSaved(true);
      setInvalid(false);
    } catch (cause) {
      setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Paper component="section" aria-label={t.tenantConfiguration} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 760 }}>
      <Typography component="h2" variant="h6" sx={{ mb: 3 }}>{t.tenantConfiguration}</Typography>
      {(error || (loadError && !current)) && (
        <Alert severity="error" action={<Button color="inherit" onClick={reload}>{t.reload}</Button>} sx={{ mb: 2 }}>
          {error || t.requestError}
        </Alert>
      )}
      {saved && <Alert severity="success" sx={{ mb: 2 }}>{t.saved}</Alert>}
      {!current && loading ? <CircularProgress aria-label={t.loading} /> : current && (
        <Box component="form" onSubmit={submit} noValidate>
          <Stack spacing={3}>
            <TextField label={t.displayName} value={name} onChange={(e) => setName(e.target.value)} error={invalid && (!name.trim() || name.length > 200)} helperText={invalid && (!name.trim() || name.length > 200) ? t.requiredName : undefined} required fullWidth />
            <TextField label={t.displayTimeZone} value={zone} onChange={(e) => setZone(e.target.value)} error={invalid && !timeZones.includes(zone)} helperText={invalid && !timeZones.includes(zone) ? t.invalidZone : undefined} select slotProps={{ select: { native: true } }}>
              {timeZones.map((value) => <option key={value} value={value}>{value}</option>)}
            </TextField>
            <Button type="submit" variant="contained" disabled={busy || !current.etag} sx={{ alignSelf: { xs: "stretch", sm: "flex-start" } }}>{busy ? t.loading : t.save}</Button>
          </Stack>
        </Box>
      )}
    </Paper>
  );
}
