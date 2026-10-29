import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Paper, Stack, Step, StepButton, Stepper, Switch, TextField, Typography } from "@mui/material";
import { createSchemaDefault, TemplateForm, validateSchema, type DynamicValuePlan, type Labels } from "@workerdock/ui";
import { ApiError, mutate, read } from "../../api/client";
import { TemplatePicker, type CatalogItem } from "../templates/TemplatePicker";
import { ScheduleEditor, type ScheduleEditorInputDto } from "../schedules/ScheduleEditor";
import { SchedulePreview, type SchedulePreviewItemDto } from "../schedules/SchedulePreview";
import { JobDetails } from "./JobDetails";
import type { JobStateDto } from "./JobActions";
import { confirmsScheduleWrite } from "./scheduleInput";
import { DynamicValuePreview } from "./DynamicValuePreview";
import { RunNowDialog } from "./RunNowDialog";
import type { DynamicValuePreviewRequest } from "./dynamicValueApi";

type Issue = { path: string; code: string };
type SqlProfile = { current: { profileId: string; name: string; server: string; allowedCatalogs: string[] }; retired: boolean };
type Revision = { jobId: string; revision: number; name: string; templateId: string; templateVersion: string; configuration: Record<string, unknown>; profileId: string | null; policy: Record<string, unknown>; dynamicValues?: DynamicValuePlan | null };
type EditorDto = { revision: Revision; version: number[]; enabled: boolean; archived: boolean; concurrencyLimit: number };
type PersistedScheduleState = "none" | "present" | "unknown" | "dirty";
type DraftSnapshot = { selected?: CatalogItem; name: string; configuration: Record<string, unknown>; dynamicValues: DynamicValuePlan | null; policy: typeof blankPolicy; scheduleEnabled: boolean; schedule: ScheduleEditorInputDto; missedMode: string };
export function selectDefaultTemplate(items: CatalogItem[]): CatalogItem | undefined {
  const first = items.find(item => item.selectable);
  if (!first) return undefined;
  const candidates = items.filter(item => item.selectable && item.descriptor.id === first.descriptor.id);
  return candidates.reduce((best, item) => compareVersions(item.descriptor.version, best.descriptor.version) > 0 ? item : best, first);
}
function compareVersions(left: string, right: string): number {
  const a = left.split(".").map(part => Number(part));
  const b = right.split(".").map(part => Number(part));
  if (a.every(Number.isSafeInteger) && b.every(Number.isSafeInteger)) {
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const difference = (a[i] ?? 0) - (b[i] ?? 0);
      if (difference) return difference;
    }
    return 0;
  }
  return left.localeCompare(right, undefined, { numeric: true });
}
const blankPolicy = { timeout: null as string | null, maxAttempts: 1, retryBaseDelay: "00:00:01", retryMaxDelay: "00:00:30", jitterRatio: 0, retryableCodes: [], retryUncertain: false, overlap: "Queue", backlogLimit: 100, priority: 50 };
const blankSchedule: ScheduleEditorInputDto = { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null };

export function JobWizard({ t, defaultZone = "UTC" }: { t: Labels; defaultZone?: string }) {
  const [catalog, setCatalog] = useState<CatalogItem[]>([]);
  const [selected, setSelected] = useState<CatalogItem>();
  const [step, setStep] = useState(0);
  const [name, setName] = useState("");
  const [configuration, setConfiguration] = useState<Record<string, unknown>>({});
  const [sqlProfiles, setSqlProfiles] = useState<{ status: "loading" | "ready" | "error"; items: SqlProfile[] }>({ status: "loading", items: [] });
  const [sqlProfilesRefresh, setSqlProfilesRefresh] = useState(0);
  const [dynamicValues, setDynamicValues] = useState<DynamicValuePlan | null>(null);
  const [policy, setPolicy] = useState({ ...blankPolicy });
  const [missedMode, setMissedMode] = useState("Skip");
  const [scheduleEnabled, setScheduleEnabled] = useState(false);
  const [scheduleState, setScheduleState] = useState<PersistedScheduleState>("none");
  const hasSchedule = scheduleState !== "none";
  const [schedule, setSchedule] = useState<ScheduleEditorInputDto>(() => ({ ...blankSchedule, zoneId: defaultZone }));
  const [job, setJob] = useState<JobStateDto & { name: string }>();
  const [issues, setIssues] = useState<Issue[]>([]);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [runKey, setRunKey] = useState("");
  const [runNowOpen, setRunNowOpen] = useState(false);
  const runOverride = useRef<string | null | undefined>(undefined);
  const runKeyJob = useRef<string | undefined>(undefined);
  const [deleteScheduleOpen, setDeleteScheduleOpen] = useState(false);
  const [staleConflict, setStaleConflict] = useState(false);
  const [recoveryDraft, setRecoveryDraft] = useState<DraftSnapshot>();
  const [reviewAcknowledged, setReviewAcknowledged] = useState(true);
  const summary = useRef<HTMLDivElement>(null);
  const localIssues = useMemo(() => selected ? validateSchema(selected.descriptor.configurationSchema, configuration, new Set((dynamicValues?.bindings ?? []).map(binding => binding.target))) : [], [selected, configuration, dynamicValues]);
  const previewRequest = useMemo<DynamicValuePreviewRequest | null>(() => {
    if (!selected || !hasDynamicAnnotation(selected.descriptor.configurationSchema) || !dynamicValues?.bindings.length) return null;
    const sampleAtUtc = new Date().toISOString();
    return { templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration, dynamicValues,
      triggerKind: scheduleEnabled ? "Scheduled" : "Manual", scheduledForUtc: scheduleEnabled ? sampleAtUtc : null,
      sampleAtUtc, referenceDateOverride: null, jobTimeout: policy.timeout };
  }, [selected, configuration, dynamicValues, policy, scheduleEnabled]);
  const labels = [t.stageTemplate, t.stageConfiguration, t.stageSchedule, t.stagePolicy, t.stageReview, t.stageActivation];
  useEffect(() => { let current = true; void read<{ items: CatalogItem[] }>("/api/templates").then(result => { if (current) { const initial = selectDefaultTemplate(result.data.items); setCatalog(result.data.items); setSelected(initial); setConfiguration(initial ? createSchemaDefault(initial.descriptor.configurationSchema, initial.descriptor.configurationSchema) as Record<string, unknown> : {}); setDynamicValues(initial && hasDynamicAnnotation(initial.descriptor.configurationSchema) ? emptyDynamicPlan("UTC") : null); } }).catch(() => { if (current) setMessage(t.requestError); }).finally(() => { if (current) setLoading(false); }); return () => { current = false; }; }, []);
  useEffect(() => { if (message) summary.current?.focus(); }, [message]);
  useEffect(() => {
    if (selected?.descriptor.id !== "workerdock.sql-server") return;
    let current = true;
    setSqlProfiles({ status: "loading", items: [] });
    void read<{ items: SqlProfile[] }>("/api/connection-profiles/").then(result => { if (current) setSqlProfiles({ status: "ready", items: result.data.items }); }).catch(() => { if (current) setSqlProfiles({ status: "error", items: [] }); });
    return () => { current = false; };
  }, [selected?.descriptor.id, sqlProfilesRefresh]);
  const saveDraft = async () => {
    if (staleConflict || !reviewAcknowledged) return false;
    if (!selected || !name.trim()) { setMessage(t.templateRequired); setStep(0); return false; }
    setBusy(true); setMessage("");
    let removingSchedule = false;
    let writingSchedule = false;
    let operationJobId = job?.jobId;
    const revision: Revision = { jobId: job?.jobId ?? "00000000-0000-0000-0000-000000000000", revision: 0, name: name.trim(), templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration, profileId: typeof configuration.profileId === "string" && configuration.profileId ? configuration.profileId : null, policy, dynamicValues: hasDynamicAnnotation(selected.descriptor.configurationSchema) ? dynamicValues : null };
    try {
      const saved = job ? await mutate<{ commandId?: string; targetId?: string; targetJobId?: string }>(`/api/jobs/${job.jobId}`, "PUT", revision, job.etag ?? undefined) : await mutate<{ commandId?: string; targetId?: string; targetJobId?: string }>("/api/jobs", "POST", revision);
      const id = job?.jobId ?? saved.targetId ?? saved.targetJobId;
      if (!id) throw new Error("JobIdUnavailable");
      operationJobId = id;
      let current = await read<EditorDto>(`/api/jobs/${id}/configuration`);
      setJob({ jobId: id, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
      if (scheduleEnabled) {
        writingSchedule = true;
        setScheduleState("dirty");
        const input = schedulePayload(schedule);
        await mutate("/api/jobs/" + id + "/schedule", "PUT", { input, policy: { mode: missedMode, latenessTolerance: "00:01:00", catchUpCount: missedMode === "CatchUp" ? 3 : 0, catchUpAge: missedMode === "CatchUp" ? "1.00:00:00" : "00:00:00" } }, current.etag ?? undefined);
        const persisted = await read<{ input: ScheduleEditorInputDto }>(`/api/jobs/${id}/schedule`);
        if (!confirmsScheduleWrite(input, persisted.data.input)) throw new Error("ScheduleConfirmationMismatch");
        setScheduleState("present");
        writingSchedule = false;
        current = await read<EditorDto>(`/api/jobs/${id}/configuration`);
      } else if (hasSchedule) {
        removingSchedule = true;
        setScheduleState("unknown");
        await mutate(`/api/jobs/${id}/schedule`, "DELETE", undefined, current.etag ?? undefined);
        current = await read<EditorDto>(`/api/jobs/${id}/configuration`);
        try {
          await read(`/api/jobs/${id}/schedule`);
          setScheduleState("present");
          setJob({ jobId: id, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
          setMessage(t.scheduleStillPresent);
          return false;
        } catch (error) {
          if (!(error instanceof ApiError) || error.status !== 404) {
            setScheduleState("unknown");
            setJob({ jobId: id, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
            setMessage(t.scheduleRemovalUnconfirmed);
            return false;
          }
        }
        setScheduleState("none");
        removingSchedule = false;
      }
      setJob({ jobId: id, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
      setStaleConflict(false);
      setMessage(t.draftSaved);
      return true;
    } catch (error) {
      if (removingSchedule) setScheduleState("unknown");
      if (writingSchedule) setScheduleState("dirty");
      const staleRevision = error instanceof ApiError && error.status === 412;
      if (staleRevision && operationJobId) recordStaleConflict();
      setMessage(staleRevision ? t.staleDraftPreserved : removingSchedule ? t.scheduleRemovalUnconfirmed : writingSchedule ? t.scheduleDraftUnconfirmed : t.requestError);
      return false;
    } finally { setBusy(false); }
  };
  const restoreDraft = () => {
    if (!recoveryDraft) return;
    setSelected(recoveryDraft.selected); setName(recoveryDraft.name); setConfiguration(recoveryDraft.configuration); setDynamicValues(recoveryDraft.dynamicValues); setPolicy(recoveryDraft.policy);
    setScheduleEnabled(recoveryDraft.scheduleEnabled); setSchedule(recoveryDraft.schedule); setMissedMode(recoveryDraft.missedMode);
    if (recoveryDraft.scheduleEnabled) setScheduleState("dirty");
    setRecoveryDraft(undefined); setReviewAcknowledged(false); setStep(4); setMessage(t.draftRestoredReviewRequired);
  };
  const recordStaleConflict = () => {
    setRecoveryDraft({ selected, name, configuration, dynamicValues, policy, scheduleEnabled, schedule, missedMode });
    setStaleConflict(true); setMessage(t.staleDraftPreserved);
  };
  const loadCurrentRevision = async () => {
    if (!job?.jobId) return;
    setBusy(true); setMessage("");
    try {
      const current = await read<EditorDto>(`/api/jobs/${job.jobId}/configuration`);
      const template = catalog.find(item => item.descriptor.id === current.data.revision.templateId && item.descriptor.version === current.data.revision.templateVersion);
      if (template) setSelected(template);
      setName(current.data.revision.name);
      setConfiguration(current.data.revision.configuration);
      setDynamicValues(current.data.revision.dynamicValues ?? (template && hasDynamicAnnotation(template.descriptor.configurationSchema) ? emptyDynamicPlan("UTC") : null));
      setPolicy({ ...blankPolicy, ...current.data.revision.policy });
      let currentHasSchedule = false;
      let currentScheduleState: PersistedScheduleState = "unknown";
      try {
        const savedSchedule = await read<{ input: ScheduleEditorInputDto; policy: { mode: string } }>(`/api/jobs/${job.jobId}/schedule`);
        const input = savedSchedule.data.input;
        setSchedule({ ...input, localOnce: input.localOnce?.slice(0, 16) ?? null, selectedOffset: input.selectedOffset?.slice(0, 6) ?? null });
        setMissedMode(savedSchedule.data.policy.mode); currentHasSchedule = true; currentScheduleState = "present";
      } catch (error) { if (error instanceof ApiError && error.status === 404) currentScheduleState = "none"; }
      setScheduleEnabled(currentHasSchedule); setScheduleState(currentScheduleState);
      setIssues([]); setJob({ jobId: job.jobId, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
      setStaleConflict(false); setStep(4); setMessage(currentScheduleState === "unknown" ? t.scheduleRemovalUnconfirmed : t.draftMustBeReviewed);
      setReviewAcknowledged(false);
    } catch { setMessage(t.requestError); }
    finally { setBusy(false); }
  };
  const preview = async (value: ScheduleEditorInputDto): Promise<SchedulePreviewItemDto[]> => {
    const result = await mutate<SchedulePreviewItemDto[]>("/api/schedules/preview", "POST", { input: schedulePayload(value), afterUtc: new Date().toISOString(), count: 5 });
    return result;
  };
  const activate = async () => {
    if (!job?.etag) return;
    if (!issues.length && localIssues.length && selected) {
      const result = await mutate<{ issues: Issue[] }>("/api/template-validation", "POST", { templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration, dynamicValues, jobTimeout: policy.timeout, triggerKind: scheduleEnabled ? "Scheduled" : "Manual" });
      setIssues(result.issues);
      if (result.issues.length) { setMessage(t.validation); return; }
    }
    setBusy(true); setMessage("");
    try {
      await mutate(`/api/jobs/${job.jobId}/actions`, "POST", { action: "Enable" }, job.etag);
      const current = await read<EditorDto>(`/api/jobs/${job.jobId}/configuration`);
      setJob({ ...job, etag: current.etag, enabled: current.data.enabled });
      setMessage(t.manualEnabled);
    } catch (error) {
      if (error instanceof ApiError && error.status === 412) recordStaleConflict();
      setMessage(error instanceof ApiError && error.status === 412 ? t.staleDraftPreserved : t.requestError);
    }
    finally { setBusy(false); }
  };
  const deleteSchedule = async () => {
    if (!job?.etag) return;
    setBusy(true); setMessage("");
    try {
      setScheduleState("unknown");
      await mutate(`/api/jobs/${job.jobId}/schedule`, "DELETE", undefined, job.etag);
      const current = await read<EditorDto>(`/api/jobs/${job.jobId}/configuration`);
      try {
        await read(`/api/jobs/${job.jobId}/schedule`);
        setScheduleState("present"); setMessage(t.scheduleStillPresent);
      } catch (error) {
        if (!(error instanceof ApiError) || error.status !== 404) {
          setScheduleState("unknown"); setMessage(t.scheduleRemovalUnconfirmed);
        } else {
          setScheduleState("none"); setScheduleEnabled(false); setDeleteScheduleOpen(false); setMessage(t.scheduleDeleted);
        }
      }
      setJob({ ...job, etag: current.etag });
    } catch (error) { setScheduleState("unknown"); setMessage(error instanceof ApiError && error.status === 412 ? t.staleDraftPreserved : t.scheduleRemovalUnconfirmed); }
    finally { setBusy(false); }
  };
  const reviewDuplicate = async (jobId: string) => {
    setBusy(true); setMessage("");
    setScheduleState("unknown"); setScheduleEnabled(false);
    try {
      const current = await read<EditorDto>(`/api/jobs/${jobId}/configuration`);
      const template = catalog.find(item => item.descriptor.id === current.data.revision.templateId && item.descriptor.version === current.data.revision.templateVersion);
      if (template) setSelected(template);
      setName(current.data.revision.name); setConfiguration(current.data.revision.configuration); setDynamicValues(current.data.revision.dynamicValues ?? (template && hasDynamicAnnotation(template.descriptor.configurationSchema) ? emptyDynamicPlan("UTC") : null)); setPolicy({ ...blankPolicy, ...current.data.revision.policy });
      setJob({ jobId, name: current.data.revision.name, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived });
      let targetScheduleState: PersistedScheduleState = "unknown";
      try {
        const savedSchedule = await read<{ input: ScheduleEditorInputDto; policy: { mode: string } }>(`/api/jobs/${jobId}/schedule`);
        const input = savedSchedule.data.input;
        setSchedule({ ...input, localOnce: input.localOnce?.slice(0, 16) ?? null, selectedOffset: input.selectedOffset?.slice(0, 6) ?? null });
        setScheduleEnabled(true); setMissedMode(savedSchedule.data.policy.mode); targetScheduleState = "present";
      } catch (error) {
        setScheduleEnabled(false);
        if (error instanceof ApiError && error.status === 404) targetScheduleState = "none";
      }
      setScheduleState(targetScheduleState);
      setIssues([]); setStep(4); setMessage(targetScheduleState === "unknown" ? t.scheduleRemovalUnconfirmed : t.draftMustBeReviewed);
    } catch { setMessage(t.requestError); }
    finally { setBusy(false); }
  };
  const refreshJob = async () => {
    if (!job) return;
    try {
      const current = await read<EditorDto>(`/api/jobs/${job.jobId}/configuration`);
      let paused = job.paused;
      try { paused = (await read<{ paused: boolean }>(`/api/jobs/${job.jobId}/schedule`)).data.paused; } catch { paused = false; }
      setJob(previous => previous ? { ...previous, etag: current.etag, enabled: current.data.enabled, archived: current.data.archived, paused } : previous);
    } catch { setMessage(t.requestError); }
  };
  const runNow = async (referenceDateOverride: string | null) => {
    if (!job?.enabled) return;
    const key = runKey && runKeyJob.current === job.jobId && runOverride.current === referenceDateOverride ? runKey : crypto.randomUUID();
    runOverride.current = referenceDateOverride; runKeyJob.current = job.jobId; setRunKey(key); setBusy(true); setMessage("");
    try { const result = await mutate<{ targetId: string }>(`/api/jobs/${job.jobId}/runs`, "POST", { requestKey: key, referenceDateOverride }); setMessage(`${t.executionAccepted}: ${result.targetId}`); setJob(current => current ? { ...current, executionId: result.targetId } : current); setRunKey(""); runOverride.current = undefined; runKeyJob.current = undefined; setRunNowOpen(false); }
    catch { setMessage(t.requestError); }
    finally { setBusy(false); }
  };
  const next = async () => {
    if (step === 4 && !reviewAcknowledged) return;
    if (step === 0 && (!selected || !name.trim())) { setMessage(t.templateRequired); return; }
    if (step === 4 && !job) { if (!(await saveDraft())) return; }
    setMessage(""); setStep(value => Math.min(5, value + 1));
  };
  const selectTemplate = (item?: CatalogItem) => { setSelected(item); setConfiguration(item ? createSchemaDefault(item.descriptor.configurationSchema, item.descriptor.configurationSchema) as Record<string, unknown> : {}); setDynamicValues(item && hasDynamicAnnotation(item.descriptor.configurationSchema) ? emptyDynamicPlan("UTC") : null); setIssues([]); };
  if (loading) return null;
  if (!catalog.length) return <Alert severity="info">{t.noTemplates}</Alert>;
  const fieldErrors = Object.fromEntries(issues.map(issue => [issue.path, issue.code]));
  const retryDelayConflict = typeof timeSpanSeconds(policy.retryBaseDelay) === "number" && typeof timeSpanSeconds(policy.retryMaxDelay) === "number" && timeSpanSeconds(policy.retryMaxDelay) < timeSpanSeconds(policy.retryBaseDelay);
  const updatePolicy = (key: string, value: unknown) => setPolicy(current => ({ ...current, [key]: value }));
  const canEnable = (scheduleState === "none" && !scheduleEnabled) || (scheduleState === "present" && scheduleEnabled);
  const safetyActions = scheduleState === "unknown" || scheduleState === "dirty" || (scheduleState === "present" && !scheduleEnabled);
  return <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 1000 }}>
    <Stack spacing={3}>
      <Typography variant="h5">{t.createJob}</Typography>
      {staleConflict && <Button disabled={busy} onClick={() => void loadCurrentRevision()}>{t.loadCurrentRevision}</Button>}
      {step === 4 && recoveryDraft && !staleConflict && <Button onClick={restoreDraft}>{t.restoreMyDraft}</Button>}
      {message && <Alert ref={summary} tabIndex={-1} role="alert" severity={message === t.requestError || message === t.validation ? "error" : "info"}>{message}{issues.length > 0 && <ul>{issues.map((issue, index) => <li key={`${issue.path}-${index}`}>{issue.path}: {issue.code}</li>)}</ul>}</Alert>}
      <Stepper activeStep={step} alternativeLabel sx={{ overflowX: "auto" }}>{labels.map((label, index) => <Step key={label}><StepButton onClick={() => setStep(index)}>{label}</StepButton></Step>)}</Stepper>
      {step === 0 && <Stack spacing={2}><TextField label={t.jobName} value={name} onChange={event => setName(event.target.value)} required autoFocus fullWidth /><TemplatePicker label={t.template} value={selected} onChange={selectTemplate} /></Stack>}
      {step === 1 && selected && <>{selected.descriptor.id === "workerdock.sql-server" && <Button disabled={sqlProfiles.status === "loading"} onClick={() => setSqlProfilesRefresh(value => value + 1)}>{t.sqlProfilesRefresh}</Button>}<TemplateForm descriptor={selected.descriptor} value={configuration} onChange={value => { setConfiguration(value); setIssues([]); }} errors={fieldErrors} dynamicValues={dynamicValues} onDynamicValuesChange={setDynamicValues} t={t} jobTimeout={policy.timeout} sqlProfiles={selected.descriptor.id === "workerdock.sql-server" ? sqlProfiles : undefined} />{localIssues.length > 0 && <Alert severity="warning">{t.localValidation}: {localIssues.length}</Alert>}<DynamicValuePreview request={previewRequest} t={t} /><Button onClick={() => { setBusy(true); void mutate<{ issues: Issue[] }>("/api/template-validation", "POST", { templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration, dynamicValues, jobTimeout: policy.timeout, triggerKind: scheduleEnabled ? "Scheduled" : "Manual" }).then(result => { setIssues(result.issues); setMessage(result.issues.length ? t.validation : t.configurationValid); }).catch(() => setMessage(t.requestError)).finally(() => setBusy(false)); }} disabled={busy}>{t.validateConfiguration}</Button></>}
      {step === 2 && <Stack spacing={2}><FormControlLabel control={<Switch checked={scheduleEnabled} onChange={event => setScheduleEnabled(event.target.checked)} />} label={t.useSchedule} />{scheduleEnabled && <ScheduleEditor value={schedule} onChange={setSchedule} preview={preview} t={t} />}{scheduleEnabled && dynamicValues && schedule.zoneId !== dynamicValues.timeZoneId && <Button onClick={() => setDynamicValues({ ...dynamicValues, timeZoneId: schedule.zoneId, bindings: dynamicValues.bindings.map(binding => binding.timeZoneId ? { ...binding, timeZoneId: schedule.zoneId } : binding) })}>{t.useScheduleTimeZone}</Button>}{job && scheduleEnabled && <Button color="error" onClick={() => setDeleteScheduleOpen(true)}>{t.deleteSchedule}</Button>}<Dialog open={deleteScheduleOpen} onClose={() => setDeleteScheduleOpen(false)}><DialogTitle>{t.confirmDeleteScheduleTitle}</DialogTitle><DialogContent>{t.confirmDeleteSchedule}</DialogContent><DialogActions><Button onClick={() => setDeleteScheduleOpen(false)}>{t.cancel}</Button><Button color="error" disabled={busy} onClick={() => void deleteSchedule()}>{t.deleteSchedule}</Button></DialogActions></Dialog></Stack>}
      {step === 3 && <Stack spacing={2}>
        <TextField label={t.timeout} type="number" value={timeSpanSeconds(policy.timeout)} onChange={event => updatePolicy("timeout", event.target.value === "" ? null : secondsToTimeSpan(Number(event.target.value)))} helperText={t.timeoutHelp} />
        <FormControlLabel control={<Switch checked={policy.timeout === null} onChange={event => updatePolicy("timeout", event.target.checked ? null : "00:01:00")} />} label={t.noTimeout} />
        <TextField label={t.maxAttempts} type="number" value={policy.maxAttempts} onChange={event => updatePolicy("maxAttempts", Number(event.target.value))} />
        <TextField label={t.retryBaseDelay} value={policy.retryBaseDelay} error={retryDelayConflict} helperText={retryDelayConflict ? t.retryDelayOrder : undefined} onChange={event => updatePolicy("retryBaseDelay", event.target.value)} />
        <TextField label={t.retryMaxDelay} value={policy.retryMaxDelay} error={retryDelayConflict} helperText={retryDelayConflict ? t.retryDelayOrder : undefined} onChange={event => updatePolicy("retryMaxDelay", event.target.value)} />
        <TextField label={t.jitterRatio} type="number" slotProps={{ htmlInput: { min: 0, max: 1, step: 0.1 } }} value={policy.jitterRatio} onChange={event => updatePolicy("jitterRatio", Number(event.target.value))} />
        <FormControlLabel control={<Switch checked={policy.retryUncertain} onChange={event => updatePolicy("retryUncertain", event.target.checked)} />} label={t.retryUncertain} />
        <TextField select label={t.overlap} helperText={t.overlapHelp} value={policy.overlap} onChange={event => updatePolicy("overlap", event.target.value)}><MenuItem value="Queue">{t.queueOverlap}</MenuItem><MenuItem value="Skip">{t.skipOverlap}</MenuItem><MenuItem value="Allow">{t.allowOverlap}</MenuItem></TextField>
        <TextField label={t.backlogLimit} type="number" value={policy.backlogLimit} onChange={event => updatePolicy("backlogLimit", Number(event.target.value))} />
        <TextField label={t.priority} type="number" value={policy.priority} onChange={event => updatePolicy("priority", Number(event.target.value))} />
        {scheduleEnabled && <TextField select label={t.missedWork} helperText={t.missedPolicyHelp} value={missedMode} onChange={event => setMissedMode(event.target.value)}><MenuItem value="Skip">{t.skipMissed}</MenuItem><MenuItem value="Coalesce">{t.coalesceMissed}</MenuItem><MenuItem value="CatchUp">{t.catchUpMissed}</MenuItem></TextField>}
      </Stack>}
      {step === 4 && !reviewAcknowledged && <Button onClick={() => setReviewAcknowledged(true)}>{t.confirmCurrentRevisionReview}</Button>}
      {step === 4 && <Stack spacing={2}><Typography variant="h6">{t.stageReview}</Typography><Typography>{name} · {selected?.descriptor.displayName} · {scheduleEnabled ? schedule.kind : scheduleState === "none" ? t.manualJobType : scheduleState === "present" ? t.scheduleStillPresent : scheduleState === "dirty" ? t.scheduleDraftUnconfirmed : t.scheduleRemovalUnconfirmed}</Typography>{scheduleState === "unknown" && <Alert severity="warning">{t.scheduleRemovalUnconfirmed}</Alert>}{scheduleState === "dirty" && <Alert severity="warning">{t.scheduleDraftUnconfirmed}</Alert>}{scheduleState === "present" && !scheduleEnabled && <Alert severity="warning">{t.scheduleStillPresent}</Alert>}{job && <Typography>{t.jobStatus}: {job.enabled ? t.enabled : t.disabled}</Typography>}{scheduleEnabled && <ScheduleEditor value={schedule} onChange={setSchedule} preview={preview} t={t} />}{dynamicValues && dynamicValues.bindings.map(binding => <Typography key={binding.target}>{binding.target}: {binding.source} · {binding.format} · {binding.timeZoneId ?? dynamicValues.timeZoneId} · {binding.months}m {binding.days}d · {binding.boundary}</Typography>)}{dynamicValues && <Typography>{t.dynamicTimeZone}: {dynamicValues.timeZoneId}</Typography>}{selected?.descriptor.id === "workerdock.sql-server" && (configuration.commandTimeoutSeconds == null || configuration.commandTimeoutSeconds === "") && <Alert severity="warning">{t.sqlTimeoutWarning}{policy.timeout ? ` ${t.sqlTimeoutLimitedByJob}` : ""}</Alert>}<Typography>{policy.timeout === null ? t.noTimeout : `${timeSpanSeconds(policy.timeout)} ${t.seconds}`} · {policy.maxAttempts} {t.attempts} · {policy.overlap}</Typography><DynamicValuePreview request={previewRequest} t={t} />{issues.length > 0 && <Alert severity="error" role="alert">{t.validation}</Alert>}{retryDelayConflict && <Alert severity="error" role="alert">{t.retryDelayOrder}</Alert>}<Button variant="contained" disabled={busy || staleConflict || !reviewAcknowledged || localIssues.length > 0 || issues.length > 0 || retryDelayConflict} onClick={() => void saveDraft()}>{t.saveDraft}</Button></Stack>}
      {step === 5 && <Stack spacing={2}><Typography variant="h6">{t.reviewAndEnable}</Typography>{scheduleState === "unknown" && <Alert severity="warning">{t.scheduleRemovalUnconfirmed}</Alert>}{scheduleState === "dirty" && <Alert severity="warning">{t.scheduleDraftUnconfirmed}</Alert>}{scheduleState === "present" && !scheduleEnabled && <Alert severity="warning">{t.scheduleStillPresent}</Alert>}{job ? <><JobDetails job={{ ...job, name }} canManage t={t} actionsDisabled={busy || staleConflict || !reviewAcknowledged || (!canEnable && !safetyActions)} safetyActions={safetyActions} onDuplicate={id => void reviewDuplicate(id)} onRefresh={() => void refreshJob()} onStale={recordStaleConflict} /><Button variant="contained" disabled={busy || staleConflict || !reviewAcknowledged || !canEnable || job.enabled || localIssues.length > 0 || issues.length > 0} onClick={() => void activate()}>{t.enableJob}</Button><Button disabled={!job.enabled || busy} onClick={() => setRunNowOpen(true)}>{t.runNow}</Button><RunNowDialog open={runNowOpen} timeZoneId={dynamicValues?.timeZoneId ?? defaultZone} supportsReferenceDate={Boolean(dynamicValues?.bindings.some(binding => binding.source === "ReferenceDate"))} busy={busy} onClose={() => setRunNowOpen(false)} onConfirm={runNow} t={t} /></> : <Alert severity="info">{t.draftSaved}</Alert>}</Stack>}
      <Stack direction="row" sx={{ justifyContent: "space-between" }}><Button disabled={step === 0 || busy} onClick={() => setStep(value => Math.max(0, value - 1))}>{t.back}</Button>{step < 5 && <Button variant="contained" disabled={busy} onClick={() => void next()}>{t.next}</Button>}</Stack>
    </Stack>
  </Paper>;
}

function schedulePayload(value: ScheduleEditorInputDto) { return { kind: value.kind, cron: value.kind === "Cron" ? value.cron : null, zoneId: value.zoneId, localOnce: value.kind === "Once" && value.localOnce ? `${value.localOnce}:00` : null, selectedOffset: value.kind === "Once" ? value.selectedOffset : null }; }
function emptyDynamicPlan(timeZoneId: string): DynamicValuePlan { return { resolverVersion: 1, timeZoneId, bindings: [] }; }
function hasDynamicAnnotation(schema: any, depth = 0): boolean {
  if (!schema || typeof schema !== "object" || depth > 32) return false;
  if (schema["x-workerDock-dynamicValue"] === true) return true;
  return Object.values(schema).some(value => Array.isArray(value)
    ? value.some(item => hasDynamicAnnotation(item, depth + 1))
    : value && typeof value === "object" && hasDynamicAnnotation(value, depth + 1));
}
function secondsToTimeSpan(seconds: number): string { const safe = Math.max(1, Math.floor(seconds)); return `${String(Math.floor(safe / 3600)).padStart(2, "0")}:${String(Math.floor((safe % 3600) / 60)).padStart(2, "0")}:${String(safe % 60).padStart(2, "0")}`; }
function timeSpanSeconds(value: string | null): number | "" { if (!value) return ""; const parts = value.split(":"); if (parts.length < 3) return ""; const dayHour = parts[0].split("."); const days = dayHour.length === 2 ? Number(dayHour[0]) : 0; const hours = Number(dayHour.at(-1)); return days * 86400 + hours * 3600 + Number(parts[1]) * 60 + Number(parts[2]); }
