import { useEffect, useMemo, useState } from "react";
import { Alert, Box, Button, FormControl, InputLabel, Link, MenuItem, Paper, Select, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from "@mui/material";
import type { Labels } from "@workerdock/ui";
import { ApiError, read } from "../../api/client";
import type { JobListItem, JobPage, JobView } from "./jobManagementTypes";

const views: JobView[] = ["current", "enabled", "disabled", "archived"];
function urlState(route: string) {
  const query = route.includes("?") ? route.slice(route.indexOf("?") + 1) : "";
  const params = new URLSearchParams(query);
  return { view: (views.includes(params.get("view") as JobView) ? params.get("view") : "current") as JobView, search: params.get("search") ?? "", cursor: params.get("cursor") ?? "" };
}

export function JobInventory({ t, route, canManage, onNavigate }: { t: Labels; route: string; canManage: boolean; onNavigate(route: string): void }) {
  const state = useMemo(() => urlState(route), [route]);
  const [searchText, setSearchText] = useState(state.search);
  const [result, setResult] = useState<JobPage>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [stale, setStale] = useState(false);
  const queryPath = `/api/jobs?view=${encodeURIComponent(state.view)}&limit=50${state.search ? `&search=${encodeURIComponent(state.search)}` : ""}${state.cursor ? `&cursor=${encodeURIComponent(state.cursor)}` : ""}`;

  useEffect(() => { setSearchText(state.search); }, [state.search]);
  useEffect(() => {
    let alive = true;
    setLoading(true); setError(false);
    void read<JobPage>(queryPath).then(({ data }) => {
      if (!alive) return;
      setResult(data); setStale(false);
    }).catch((cause) => {
      if (!alive) return;
      setError(true); setStale(Boolean(result));
      if (cause instanceof ApiError && cause.status === 403) setError(true);
    }).finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [queryPath]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const normalized = searchText.trim();
      if (normalized === state.search) return;
      onNavigate(`jobs?view=${state.view}${normalized ? `&search=${encodeURIComponent(normalized)}` : ""}`);
    }, 300);
    return () => window.clearTimeout(timer);
  }, [searchText, state.search, state.view, onNavigate]);

  const setView = (view: JobView) => onNavigate(`jobs?view=${view}${state.search ? `&search=${encodeURIComponent(state.search)}` : ""}`);
  const rows = result?.items ?? [];
  const label = (job: JobListItem) => job.archived ? t.jobViewArchived : job.enabled ? t.jobViewEnabled : t.jobViewDisabled;
  const open = (id: string) => onNavigate(`jobs/${id}${state.search || state.view !== "current" ? `?returnView=${state.view}${state.search ? `&returnSearch=${encodeURIComponent(state.search)}` : ""}` : ""}`);
  return <Stack spacing={2}>
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
      <FormControl sx={{ minWidth: 170 }}><InputLabel id="job-view-label">{t.jobInventoryView}</InputLabel><Select labelId="job-view-label" label={t.jobInventoryView} value={state.view} onChange={event => setView(event.target.value as JobView)}>
        <MenuItem value="current">{t.jobViewCurrent}</MenuItem><MenuItem value="enabled">{t.jobViewEnabled}</MenuItem><MenuItem value="disabled">{t.jobViewDisabled}</MenuItem><MenuItem value="archived">{t.jobViewArchived}</MenuItem>
      </Select></FormControl>
      <TextField label={t.jobSearchLabel} value={searchText} onChange={event => setSearchText(event.target.value)} slotProps={{ htmlInput: { maxLength: 100 } }} fullWidth />
      {canManage && <Button variant="contained" onClick={() => onNavigate("jobs/new")}>{t.createJob}</Button>}
    </Stack>
    {error && <Alert severity="error" action={<Button color="inherit" onClick={() => { setError(false); setLoading(true); void read<JobPage>(queryPath).then(({ data }) => { setResult(data); setStale(false); }).catch(() => setError(true)).finally(() => setLoading(false)); }}>{t.retry}</Button>}>{stale ? t.jobInventoryStale : t.jobInventoryLoadError}</Alert>}
    {stale && !error && <Alert severity="warning">{t.jobInventoryStale}</Alert>}
    {loading && !result && <Typography role="status">{t.loading}</Typography>}
    {!loading && !error && !rows.length && <Paper variant="outlined" sx={{ p: 3 }}><Typography>{state.search ? t.jobNoSearchResults : t.jobNoSavedJobs}</Typography></Paper>}
    {!!rows.length && <Box sx={{ overflowX: "auto" }}><Table aria-label={t.jobs} size="small"><TableHead><TableRow><TableCell>{t.jobName}</TableCell><TableCell>{t.template}</TableCell><TableCell>{t.jobStatus}</TableCell><TableCell>{t.jobScheduleLabel}</TableCell></TableRow></TableHead><TableBody>{rows.map(job => <TableRow key={job.jobId} hover>
      <TableCell><Link href={`#/jobs/${job.jobId}`} onClick={event => { event.preventDefault(); open(job.jobId); }}>{job.name}</Link></TableCell><TableCell>{job.templateId} · {job.templateVersion}</TableCell><TableCell>{label(job)}</TableCell><TableCell>{job.scheduleKind ? `${job.scheduleKind}${job.schedulePaused ? ` · ${t.jobPausedLabel}` : ""}` : t.jobManualLabel}</TableCell>
    </TableRow>)}</TableBody></Table></Box>}
    {result?.nextCursor && <Button onClick={() => onNavigate(`jobs?view=${state.view}${state.search ? `&search=${encodeURIComponent(state.search)}` : ""}&cursor=${encodeURIComponent(result.nextCursor!)}`)}>{t.next}</Button>}
  </Stack>;
}
