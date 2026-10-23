import { Alert, Paper, Stack, Typography } from "@mui/material";
import { JobActions, type JobStateDto } from "./JobActions";
import type { Labels } from "@workerdock/ui";

export function JobDetails({ job, canManage, onRefresh, onDuplicate, onStale, actionsDisabled = false, safetyActions = false, t }: { job: JobStateDto & { name?: string; recentExecution?: string }; canManage: boolean; onRefresh(): void; onDuplicate?(jobId: string): void; onStale?(): void; actionsDisabled?: boolean; safetyActions?: boolean; t: Labels }) {
  return <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Stack spacing={2}>
    <Typography variant="h6">{job.name ?? "Job details"}</Typography>
    <Typography>{t.jobStatus}: {job.archived ? t.archived : job.enabled ? t.enabled : t.disabled}</Typography>
    {job.paused && <Alert severity="info">{t.schedulePaused}</Alert>}
    {job.recentExecution && <Typography>{t.executionHistory}: {job.recentExecution}</Typography>}
    {canManage && <JobActions job={job} onRefresh={onRefresh} onDuplicate={onDuplicate} onStale={onStale} blocked={actionsDisabled} safetyActions={safetyActions} t={t} />}
  </Stack></Paper>;
}
