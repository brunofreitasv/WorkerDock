import { useState } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Stack, Switch, TextField } from "@mui/material";
import { mutate } from "../../api/client";
import type { Labels } from "@workerdock/ui";

export function StopDialog({ open, executionId, onClose, t }: { open: boolean; executionId: string; onClose(): void; t: Labels }) {
  const [force, setForce] = useState(false);
  const [reason, setReason] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const stop = async () => {
    setBusy(true); setMessage("");
    try { await mutate(`/api/executions/${executionId}/stop`, "POST", { force, confirmForce: force, reason }); setMessage(force ? `${t.forceStopRequested} ${t.remoteOutcome}` : t.gracefulStopRequested); }
    catch { setMessage(t.stopError); }
    finally { setBusy(false); }
  };
  return <Dialog open={open} onClose={onClose} aria-labelledby="stop-dialog-title"><DialogTitle id="stop-dialog-title">{t.stopExecution}</DialogTitle><DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
    <Alert severity="warning">{t.stopHelp} {t.remoteOutcome}</Alert>
    {message && <Alert role="status" severity="info">{message}</Alert>}
    <TextField label={t.stopReason} value={reason} onChange={event => setReason(event.target.value)} required />
    <FormControlLabel control={<Switch checked={force} onChange={event => setForce(event.target.checked)} />} label={t.forceStop} />
  </Stack></DialogContent><DialogActions><Button onClick={onClose}>{t.cancel}</Button><Button color="error" onClick={() => void stop()} disabled={busy || !reason.trim()}>{t.confirmStop}</Button></DialogActions></Dialog>;
}
