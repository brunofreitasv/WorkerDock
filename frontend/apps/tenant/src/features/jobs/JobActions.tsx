import { useState } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack } from "@mui/material";
import { ApiError, mutate } from "../../api/client";
import { StopDialog } from "../executions/StopDialog";
import type { Labels } from "@workerdock/ui";

export interface JobStateDto { jobId: string; etag?: string | null; enabled: boolean; archived: boolean; paused?: boolean; projectionPending?: boolean; executionId?: string }
export function JobActions({ job, onRefresh, onDuplicate, onStale, blocked = false, safetyActions = false, t }: { job: JobStateDto; onRefresh(): void; onDuplicate?(jobId: string): void; onStale?(): void; blocked?: boolean; safetyActions?: boolean; t: Labels }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState("");
  const [stopOpen, setStopOpen] = useState(false);
  const action = async (name: string) => {
    if (!job.etag || blocked) return;
    setBusy(true); setError("");
    try {
      await mutate(`/api/jobs/${job.jobId}/actions`, "POST", { action: name }, job.etag);
      onRefresh();
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 412) {
        setError(t.staleDraftPreserved); onStale?.();
      } else setError(t.actionError);
    } finally { setBusy(false); setConfirm(""); }
  };
  const duplicate = async () => {
    if (!job.etag || blocked) return;
    setBusy(true); setError("");
    try { const targetJobId = crypto.randomUUID(); await mutate(`/api/jobs/${job.jobId}/actions`, "POST", { action: "Duplicate", targetJobId }, job.etag); onDuplicate?.(targetJobId); setError(t.draftMustBeReviewed); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.staleDraftPreserved : t.actionError); }
    finally { setBusy(false); }
  };
  return <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
    {error && <Alert role="status" severity="info">{error}</Alert>}
    {job.executionId && <><Button onClick={() => setStopOpen(true)} disabled={busy || blocked}>{t.stopExecution}</Button><StopDialog open={stopOpen} executionId={job.executionId} onClose={() => setStopOpen(false)} t={t} /></>}
    {!job.archived && (job.enabled || safetyActions ? <>
      {job.paused ? <Button disabled={busy || blocked} onClick={() => void action("ResumeSchedule")}>{t.resumeSchedule}</Button> : <Button disabled={busy || blocked} onClick={() => void action("PauseSchedule")}>{t.pauseSchedule}</Button>}
      <Button disabled={busy || blocked} onClick={() => setConfirm("Disable")}>{t.disableJob}</Button>
    </> : <Button disabled={busy || blocked} onClick={() => void action("Enable")}>{t.enableJobAction}</Button>)}
    {!job.archived && <><Button disabled={busy || blocked} onClick={() => void duplicate()}>{t.duplicateJob}</Button><Button disabled={busy || blocked} onClick={() => setConfirm("Archive")}>{t.archiveJob}</Button></>}
    <Dialog open={!!confirm} onClose={() => setConfirm("")}><DialogTitle>{confirm === "Archive" ? t.confirmArchiveTitle : t.confirmDisableTitle}</DialogTitle><DialogContent>{confirm === "Archive" ? t.confirmArchive : t.confirmDisable}</DialogContent><DialogActions><Button onClick={() => setConfirm("")}>{t.cancel}</Button><Button disabled={busy || blocked} onClick={() => void action(confirm)}>{confirm === "Archive" ? t.archiveJob : t.disableJob}</Button></DialogActions></Dialog>
  </Stack>;
}
