import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Box, Button, Paper, Stack, Tab, Tabs, Typography } from "@mui/material";
import type { Labels } from "@workerdock/ui";
import type { TemplateDescriptorDto } from "@workerdock/ui";
import { ApiError, mutate, read } from "../../api/client";
import { JobActions } from "./JobActions";
import { JobExecutionControls } from "./JobExecutionControls";
import { ManagedJobEditor } from "./ManagedJobEditor";
import type { JobDefinitionDraft } from "./JobDefinitionDraft";
import { RunNowDialog } from "./RunNowDialog";

type Detail = { editor: { revision: Record<string, unknown>; version?: unknown; enabled: boolean; archived: boolean; concurrencyLimit: number; pauseOnNeedsReview: boolean }; schedule: Record<string, unknown> | null; missedPolicy: Record<string, unknown> | null; schedulePaused: boolean; previousRemovedSchedule?: Record<string, unknown> | null };
type ControlsReadDto = { limit: number; pauseOnNeedsReview: boolean };
type HistoryPage = { items: Array<{ executionId: string; jobRevision: number; triggerKind: string; state: string; createdAtUtc: string; startedAtUtc?: string | null; duration?: string | number | null }>; nextCursor: string | null };

export function ManagedJobDetail({ jobId, returnRoute, t, canManage, onNavigate, onDirtyChange }: { jobId: string; returnRoute: string; t: Labels; canManage: boolean; onNavigate(route: string): void; onDirtyChange?(dirty: boolean): void }) {
  const [detail, setDetail] = useState<Detail>();
  const [viewerControls, setViewerControls] = useState<ControlsReadDto>();
  const [etag, setEtag] = useState<string | null>(null);
  const [history, setHistory] = useState<HistoryPage>();
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState(false);
  const [tab, setTab] = useState(0);
  const [editing, setEditing] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [descriptor, setDescriptor] = useState<TemplateDescriptorDto | null>(null);
  const [descriptorError, setDescriptorError] = useState(false);
  const [runNowOpen, setRunNowOpen] = useState(false);
  const [runNowBusy, setRunNowBusy] = useState(false);
  const [runNowMessage, setRunNowMessage] = useState("");
  const [executionId, setExecutionId] = useState<string>();
  const runKey = useRef<string | undefined>(undefined);
  const [error, setError] = useState(false);
  const load = useCallback(() => {
    setError(false);
    if (!canManage) {
      setDetail(undefined); setEtag(null); setViewerControls(undefined);
      void read<ControlsReadDto>(`/api/jobs/${jobId}/execution-controls`).then(({ data }) => setViewerControls(data)).catch(() => setError(true));
      return;
    }
    setViewerControls(undefined);
    void read<Detail>(`/api/jobs/${jobId}`).then(({ data, etag: tag }) => { setDetail(data); setEtag(tag); }).catch(() => setError(true));
  }, [jobId, canManage]);
  useEffect(() => { load(); }, [load]);
  const loadHistory = useCallback(() => {
    setHistoryLoading(true); setHistoryError(false);
    void read<HistoryPage>(`/api/jobs/${jobId}/executions?limit=50`).then(({ data }) => setHistory(data)).catch(() => setHistoryError(true)).finally(() => setHistoryLoading(false));
  }, [jobId]);
  useEffect(() => { if (tab === 4) loadHistory(); }, [tab, loadHistory]);
  useEffect(() => {
    if (!editing || !detail) return;
    const templateId = detail.editor.revision.templateId;
    const templateVersion = detail.editor.revision.templateVersion;
    setDescriptor(null); setDescriptorError(false);
    void read<{ items: Array<{ descriptor: TemplateDescriptorDto }> }>("/api/templates").then(({ data }) => {
      const found = data.items.find(item => item.descriptor.id === templateId && item.descriptor.version === templateVersion);
      if (found) setDescriptor(found.descriptor); else setDescriptorError(true);
    }).catch(() => setDescriptorError(true));
  }, [editing, detail]);
  const updateDirty = useCallback((value: boolean) => { setDirty(value); onDirtyChange?.(value); }, [onDirtyChange]);
  if (!canManage) {
    if (error && !viewerControls) return <Alert severity="error" action={<Button onClick={load}>{t.retry}</Button>}>{t.jobInventoryLoadError}</Alert>;
    if (!viewerControls) return <Typography role="status">{t.loading}</Typography>;
    return <Paper component="section" aria-label={t.jobSectionOverview} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 900 }}><JobExecutionControls jobId={jobId} limit={viewerControls.limit} pauseOnNeedsReview={viewerControls.pauseOnNeedsReview} canManage={false} t={t} onSaved={load} /></Paper>;
  }
  if (error && !detail) return <Alert severity="error" action={<Button onClick={load}>{t.retry}</Button>}>{t.jobInventoryLoadError}</Alert>;
  if (!detail) return <Typography role="status">{t.loading}</Typography>;
  const revision = detail.editor.revision;
  const title = String(revision.name ?? jobId);
  const definition: JobDefinitionDraft = { revision, schedule: detail.schedule, missedPolicy: detail.missedPolicy };
  const status = detail.editor.archived ? t.jobViewArchived : detail.editor.enabled ? t.jobViewEnabled : t.jobViewDisabled;
  const refresh = () => { setEditing(false); load(); };
  const guardedNavigate = (route: string) => { if (dirty && !window.confirm(t.jobDiscardConfirm)) return; updateDirty(false); onNavigate(route); };
  const runNow = async (referenceDateOverride: string | null) => {
    setRunNowBusy(true); setRunNowMessage("");
    try {
      runKey.current ??= crypto.randomUUID();
      const result = await mutate<{ targetId: string }>(`/api/jobs/${jobId}/runs`, "POST", { requestKey: runKey.current, referenceDateOverride });
      setExecutionId(result.targetId); setRunNowMessage(`${t.executionAccepted}: ${result.targetId}`); runKey.current = undefined; setRunNowOpen(false);
    } catch { setRunNowMessage(t.actionError); }
    finally { setRunNowBusy(false); }
  };
  return <Stack spacing={2}>
    {error && <Alert severity="error" action={<Button onClick={load}>{t.retry}</Button>}>{t.jobInventoryLoadError}</Alert>}
    <Paper component="section" aria-label={title} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Stack spacing={2}><Typography component="h2" variant="h5">{title}</Typography><Typography>{String(revision.templateId ?? "")} · {String(revision.templateVersion ?? "")} · {status}</Typography><Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ flexWrap: "wrap", alignItems: { sm: "center" } }}><Button onClick={() => guardedNavigate(returnRoute)}>{t.jobs}</Button>{!detail.editor.archived && <Button onClick={() => setEditing(true)}>{t.jobEditAction}</Button>}{detail.editor.enabled && !detail.editor.archived && <Button disabled={dirty} onClick={() => setRunNowOpen(true)}>{t.runNow}</Button>}<JobActions job={{ jobId, name: title, etag, enabled: detail.editor.enabled, archived: detail.editor.archived, paused: detail.schedulePaused, hasSchedule: Boolean(detail.schedule), executionId }} t={t} onRefresh={load} onStale={load} blocked={dirty} /></Stack>{runNowMessage && <Alert severity="info">{runNowMessage}{executionId && <Button onClick={() => onNavigate(`execution:${executionId}`)}>{t.openExecution}</Button>}</Alert>}</Stack></Paper>
    <RunNowDialog open={runNowOpen} timeZoneId={String((revision.dynamicValues as { timeZoneId?: string } | undefined)?.timeZoneId ?? detail.schedule?.zoneId ?? "UTC")} supportsReferenceDate={Boolean((revision.dynamicValues as { bindings?: Array<{ source: string }> } | undefined)?.bindings?.some(binding => binding.source === "ReferenceDate"))} busy={runNowBusy} onClose={() => setRunNowOpen(false)} onConfirm={runNow} t={t} />
    {editing ? descriptor ? <ManagedJobEditor jobId={jobId} initial={definition} etag={etag} descriptor={descriptor} t={t} onSaved={refresh} onCancel={() => { updateDirty(false); setEditing(false); }} onDirtyChange={updateDirty} /> : <Alert severity={descriptorError ? "error" : "info"}>{descriptorError ? t.jobTemplateUnavailable : t.loading}<Button onClick={() => setEditing(false)}>{t.cancel}</Button></Alert> : <>
      <Box sx={{ borderBottom: 1, borderColor: "divider" }}><Tabs aria-label={t.jobs} value={tab} onChange={(_, value: number) => setTab(value)} variant="scrollable" scrollButtons="auto" allowScrollButtonsMobile><Tab label={t.jobSectionOverview} /><Tab label={t.jobSectionConfiguration} /><Tab label={t.jobSectionSchedule} /><Tab label={t.jobSectionPolicy} /><Tab label={t.jobSectionHistory} /></Tabs></Box>
      {tab === 0 && <Paper component="section" aria-label={t.jobSectionOverview} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Stack spacing={2}><Typography component="h2" variant="h6">{t.jobSectionOverview}</Typography><JobExecutionControls jobId={jobId} limit={detail.editor.concurrencyLimit} pauseOnNeedsReview={detail.editor.pauseOnNeedsReview} overlap={String((revision.policy as Record<string, unknown> | undefined)?.overlap ?? "Queue")} canManage={canManage} t={t} onSaved={load} /></Stack></Paper>}
      {tab === 1 && <Paper component="section" aria-label={t.jobSectionConfiguration} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Typography component="h2" variant="h6">{t.jobSectionConfiguration}</Typography><pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{JSON.stringify({ configuration: revision.configuration, profileId: revision.profileId, dynamicValues: revision.dynamicValues }, null, 2)}</pre></Paper>}
      {tab === 2 && <Paper component="section" aria-label={t.jobSectionSchedule} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Typography component="h2" variant="h6">{t.jobSectionSchedule}</Typography><pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{JSON.stringify(detail.schedule ?? detail.previousRemovedSchedule, null, 2)}</pre>{detail.previousRemovedSchedule && <Alert severity="warning">{t.jobLegacyScheduleNotice}</Alert>}</Paper>}
      {tab === 3 && <Paper component="section" aria-label={t.jobSectionPolicy} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}><Typography component="h2" variant="h6">{t.jobSectionPolicy}</Typography><pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{JSON.stringify({ policy: revision.policy, missedPolicy: detail.missedPolicy }, null, 2)}</pre></Paper>}
      {tab === 4 && <Paper component="section" aria-label={t.jobSectionHistory} variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
        <Typography component="h2" variant="h6" sx={{ mb: 2 }}>{t.jobSectionHistory}</Typography>
        {historyError && <Alert severity="error" action={<Button onClick={loadHistory}>{t.retry}</Button>}>{t.jobHistoryLoadError}</Alert>}
        {historyLoading && !history && <Typography role="status">{t.loading}</Typography>}
        {history && !history.items.length && <Typography>{t.jobNoHistory}</Typography>}
        {history?.items.map(item => <Stack key={item.executionId} direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ py: 1, borderBottom: "1px solid", borderColor: "divider" }}>
          <Typography><a href={`#/execution:${item.executionId}`}>{item.executionId}</a></Typography><Typography>{item.triggerKind} · {item.state} · {t.jobRevisionLabel} {item.jobRevision}</Typography><Typography>{t.historyCreated}: {item.createdAtUtc}</Typography>{item.startedAtUtc && <Typography>{t.historyStarted}: {item.startedAtUtc}</Typography>}{item.duration != null && <Typography>{t.historyDuration}: {String(item.duration)}</Typography>}
        </Stack>)}
        {history?.nextCursor && <Button disabled={historyLoading} onClick={() => { setHistoryLoading(true); setHistoryError(false); void read<HistoryPage>(`/api/jobs/${jobId}/executions?limit=50&cursor=${encodeURIComponent(history.nextCursor!)}`).then(({ data }) => setHistory({ ...data, items: [...history.items, ...data.items] })).catch(() => setHistoryError(true)).finally(() => setHistoryLoading(false)); }}>{t.next}</Button>}
      </Paper>}
    </>}
  </Stack>;
}
