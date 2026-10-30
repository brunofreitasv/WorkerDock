import { useEffect, useState } from "react";
import { Alert, Button, FormControlLabel, Stack, Switch, TextField, Typography } from "@mui/material";
import type { Labels } from "@workerdock/ui";
import { mutate } from "../../api/client";

export function JobExecutionControls({ jobId, limit, pauseOnNeedsReview, canManage, overlap, t, onSaved }: {
  jobId: string;
  limit: number;
  pauseOnNeedsReview: boolean;
  canManage: boolean;
  overlap?: string;
  t: Labels;
  onSaved(): void;
}) {
  const [nextLimit, setNextLimit] = useState(limit);
  const [nextPause, setNextPause] = useState(pauseOnNeedsReview);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  useEffect(() => { setNextLimit(limit); setNextPause(pauseOnNeedsReview); }, [limit, pauseOnNeedsReview]);
  const reviewLabel = overlap === "Skip" ? t.skipNewRunsWhileReview : overlap === undefined ? t.jobReviewHoldLabel : t.waitForReviewBeforeStarting;
  const save = async () => {
    setBusy(true); setError(false);
    try {
      await mutate(`/api/jobs/${jobId}/execution-controls`, "PUT", { limit: nextLimit, pauseOnNeedsReview: nextPause });
      onSaved();
    } catch { setError(true); }
    finally { setBusy(false); }
  };
  return <Stack spacing={1}>
    <Typography variant="h6">{t.jobExecutionControls}</Typography>
    <TextField label={t.jobCapacity} type="number" value={nextLimit} disabled={!canManage || busy} slotProps={{ htmlInput: { min: 0, step: 1 } }} onChange={event => setNextLimit(Math.max(0, Math.floor(Number(event.target.value) || 0)))} helperText={nextLimit === 0 ? t.jobCapacityPausedHelp : t.jobCapacityHelp} />
    {overlap !== "Allow" && <FormControlLabel control={<Switch checked={nextPause} disabled={!canManage || busy} onChange={event => setNextPause(event.target.checked)} />} label={reviewLabel} />}
    {overlap === "Allow" && <Typography>{t.allowOverlapCapacityHelp}</Typography>}
    {error && <Alert severity="error">{t.jobControlsSaveError}</Alert>}
    {canManage && <Button disabled={busy || nextLimit === limit && nextPause === pauseOnNeedsReview} onClick={() => void save()}>{busy ? t.loading : t.save}</Button>}
  </Stack>;
}
