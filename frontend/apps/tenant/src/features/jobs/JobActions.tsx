import { useState } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack } from "@mui/material";
import { ApiError, mutate, read } from "../../api/client";
import { StopDialog } from "../executions/StopDialog";
import type { Labels } from "@workerdock/ui";

export interface JobStateDto { jobId: string; etag?: string | null; enabled: boolean; archived: boolean; paused?: boolean; projectionPending?: boolean; executionId?: string }
export function JobActions({ job, onRefresh, onDuplicate, t }: { job: JobStateDto; onRefresh(): void; onDuplicate?(jobId: string): void; t: Labels }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState("");
  const [stopOpen, setStopOpen] = useState(false);
  const action = async (name: string) => {
    if (!job.etag) return;
    setBusy(true); setError("");
    try {
      await mutate(`/api/jobs/${job.jobId}/actions`, "POST", { action: name }, job.etag);
      onRefresh();
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 412) {
        try { await read(`/api/jobs/${job.jobId}/configuration`); } catch { /* keep local draft and current details */ }
        setError(t.staleDraftPreserved); onRefresh();
      } else setError(t.actionError);
    } finally { setBusy(false); setConfirm(""); }
  };
  const duplicate = async () => {
    if (!job.etag) return;
    setBusy(true); setError("");
    try { const targetJobId = crypto.randomUUID(); await mutate(`/api/jobs/${job.jobId}/actions`, "POST", { action: "Duplicate", targetJobId }, job.etag); onDuplicate?.(targetJobId); setError(t.draftMustBeReviewed); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.staleDraftPreserved : t.actionError); }
    finally { setBusy(false); }
  };
  return <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
    {error && <Alert role="status" severity="info">{error}</Alert>}
    {job.executionId && <><Button onClick={() => setStopOpen(true)} disabled={busy}>{t.stopExecution}</Button><StopDialog open={stopOpen} executionId={job.executionId} onClose={() => setStopOpen(false)} t={t} /></>}
    {!job.archived && (job.enabled ? <>
      {job.paused ? <Button disabled={busy} onClick={() => void action("ResumeSchedule")}>{t.resumeSchedule}</Button> : <Button disabled={busy} onClick={() => void action("PauseSchedule")}>{t.pauseSchedule}</Button>}
      <Button disabled={busy} onClick={() => setConfirm("Disable")}>{t.disableJob}</Button>
    </> : <Button disabled={busy} onClick={() => void action("Enable")}>{t.enableJobAction}</Button>)}
    {!job.archived && <><Button disabled={busy} onClick={() => void duplicate()}>{t.duplicateJob}</Button><Button disabled={busy} onClick={() => setConfirm("Archive")}>{t.archiveJob}</Button></>}
    <Dialog open={!!confirm} onClose={() => setConfirm("")}><DialogTitle>{confirm === "Archive" ? t.confirmArchiveTitle : t.confirmDisableTitle}</DialogTitle><DialogContent>{confirm === "Archive" ? t.confirmArchive : t.confirmDisable}</DialogContent><DialogActions><Button onClick={() => setConfirm("")}>{t.cancel}</Button><Button onClick={() => void action(confirm)}>{confirm === "Archive" ? t.archiveJob : t.disableJob}</Button></DialogActions></Dialog>
  </Stack>;
}
