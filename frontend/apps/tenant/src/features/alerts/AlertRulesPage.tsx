import { useEffect, useRef, useState } from "react";
import { Alert, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, FormControlLabel, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { ApiError, mutate, read } from "../../api/client";
import type { Labels } from "../../../../../packages/ui/src/preferences";
import { createRuleDraft, createSinkDraft, rulePayload, sinkPayload, type AlertRule, type AlertSink } from "./alertDrafts";

const conditions = ["RepeatedFailure", "RetryExhausted", "Timeout", "LongRunning", "MissedExecution", "WorkerUnavailable"] as const;
const forbiddenHeaders = new Set(["host", "content-length", "transfer-encoding", "connection", "keep-alive", "proxy-connection", "upgrade", "te", "trailer", "expect"]);
const controls = /[\u0000-\u001f\u007f]/;
interface HeaderReference { name: string; secretReference: string }
interface Incident { incidentId: string; ruleName: string; condition: string; subjectKey: string; state: string; openedAtUtc: string; lastObservedAtUtc: string; resolvedAtUtc: string | null; safeCode: string | null }
interface Delivery { deliveryId: string; incidentId: string; kind: string; status: string; attemptCount: number; nextAvailableAtUtc: string; completedAtUtc: string | null; safeCode: string | null }
const integer = (value: number, min: number, max: number) => Number.isInteger(value) && value >= min && value <= max;
function safeSink(sink: AlertSink): AlertSink {
  const config = sink.configuration as AlertSink["configuration"] & { headers?: Array<{ name?: unknown; secretReference?: unknown; value?: unknown }> };
  return { ...sink, configuration: { url: typeof config?.url === "string" ? config.url : "", headers: Array.isArray(config?.headers) ? config.headers.map(header => ({ name: typeof header.name === "string" ? header.name : "", secretReference: typeof header.secretReference === "string" ? header.secretReference : "" })) : [] } };
}
function validUrl(value: string) {
  try { const url = new URL(value); return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password && !url.hash; } catch { return false; }
}
function validHeaderName(name: string) {
  return /^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$/.test(name) && !forbiddenHeaders.has(name.toLowerCase());
}
function validSecretReference(secretReference: string) {
  return secretReference.trim().length > 0 && secretReference.length <= 200 && !controls.test(secretReference);
}
function validHeaders(headers: HeaderReference[]) {
  return headers.length <= 32 && headers.every(h => validHeaderName(h.name) && validSecretReference(h.secretReference)) && new Set(headers.map(h => h.name.toLowerCase())).size === headers.length;
}

export function AlertRulesPage({ t, canManage }: { t: Labels; canManage: boolean }) {
  const [rules, setRules] = useState<AlertRule[]>([]); const [sinks, setSinks] = useState<AlertSink[]>([]);
  const [ruleId, setRuleId] = useState(""); const [sinkId, setSinkId] = useState("");
  const [ruleDraft, setRuleDraft] = useState<AlertRule>(); const [sinkDraft, setSinkDraft] = useState<AlertSink>();
  const [ruleEtag, setRuleEtag] = useState<string>(); const [sinkEtag, setSinkEtag] = useState<string>();
  const [creatingSink, setCreatingSink] = useState(false);
  const [creatingRule, setCreatingRule] = useState(false);
  const [sinksLoaded, setSinksLoaded] = useState(false);
  const [createdSink, setCreatedSink] = useState(false);
  const [incidents, setIncidents] = useState<Incident[]>([]); const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [error, setError] = useState(""); const [saved, setSaved] = useState(false); const [busy, setBusy] = useState(false); const [invalid, setInvalid] = useState(false);
  const [ruleDirty, setRuleDirty] = useState(false); const [sinkDirty, setSinkDirty] = useState(false);
  const [pendingSelection, setPendingSelection] = useState<{ kind: "rule" | "sink"; id: string | undefined }>();
  const requestGeneration = useRef({ rule: 0, sink: 0 });
  const permissionGeneration = useRef(0);
  const applySelection = (kind: "rule" | "sink", id: string | undefined) => {
    requestGeneration.current[kind]++;
    setPendingSelection(undefined); setError(""); setSaved(false); setInvalid(false);
    if (kind === "rule") {
      if (id === undefined) {
        const defaultSink = sinks.find(s => s.enabled);
        const draft = createRuleDraft(crypto.randomUUID(), defaultSink?.id ?? "");
        setRuleDraft(draft); setRuleId(""); setRuleEtag(undefined); setCreatingRule(true); setRuleDirty(true);
      } else { setCreatingRule(false); setRuleDraft(undefined); setRuleId(id); setRuleEtag(undefined); setRuleDirty(false); }
    } else {
      if (id === undefined) {
        const draft = createSinkDraft(crypto.randomUUID());
        setSinkDraft(draft); setSinkId(draft.id); setSinkEtag(undefined); setCreatingSink(true); setSinkDirty(true);
      } else { setCreatingSink(false); setSinkDraft(undefined); setSinkId(id); setSinkEtag(undefined); setSinkDirty(false); }
    }
  };
  const requestSelection = (kind: "rule" | "sink", id: string | undefined) => {
    if (kind === "rule" ? ruleDirty : sinkDirty) setPendingSelection({ kind, id });
    else applySelection(kind, id);
  };
  const load = () => {
    setError("");
    const generation = permissionGeneration.current;
    const tasks: Promise<unknown>[] = [read<Incident[]>("/api/alerts/incidents?limit=100").then(x => setIncidents(x.data)), read<Delivery[]>("/api/alerts/deliveries?limit=100").then(x => setDeliveries(x.data))];
    if (canManage) {
      tasks.push(read<AlertRule[]>("/api/alert-rules").then(x => { if (generation !== permissionGeneration.current || !canManage) return; setRules(x.data); setRuleId(id => id || x.data[0]?.id || ""); }), read<AlertSink[]>("/api/alert-sinks").then(x => { if (generation !== permissionGeneration.current || !canManage) return; const data = x.data.map(safeSink); setSinks(data); setSinksLoaded(true); setSinkId(id => id || data[0]?.id || ""); }));
    }
    void Promise.all(tasks).catch(() => setError(t.requestError));
  };
  useEffect(() => { void load(); }, [canManage, t.requestError]);
  useEffect(() => {
    if (!canManage || !ruleId || creatingRule) return;
    const generation = requestGeneration.current.rule;
    let active = true;
    void read<AlertRule>(`/api/alert-rules/${ruleId}`).then(({ data, etag }) => {
      if (!active || generation !== requestGeneration.current.rule) return;
      setRuleDraft(data); setRuleEtag(etag ?? undefined); setRuleDirty(false);
    }).catch(() => { if (active && generation === requestGeneration.current.rule) setError(t.requestError); });
    return () => { active = false; };
  }, [canManage, ruleId, creatingRule, t.requestError]);
  useEffect(() => {
    if (!canManage || !sinkId || creatingSink) return;
    const generation = requestGeneration.current.sink;
    let active = true;
    void read<AlertSink>(`/api/alert-sinks/${sinkId}`).then(({ data, etag }) => {
      if (!active || generation !== requestGeneration.current.sink) return;
      setSinkDraft(safeSink(data)); setSinkEtag(etag ?? undefined); setSinkDirty(false);
    }).catch(() => { if (active && generation === requestGeneration.current.sink) setError(t.requestError); });
    return () => { active = false; };
  }, [canManage, sinkId, creatingSink, t.requestError]);
  useEffect(() => {
    if (canManage) return;
    permissionGeneration.current++;
    requestGeneration.current.rule++; requestGeneration.current.sink++; setRules([]); setSinks([]); setRuleId(""); setSinkId(""); setRuleDraft(undefined); setSinkDraft(undefined);
    setRuleEtag(undefined); setSinkEtag(undefined); setCreatingRule(false); setCreatingSink(false); setSinksLoaded(false); setRuleDirty(false); setSinkDirty(false); setPendingSelection(undefined); setSaved(false); setError(""); setInvalid(false);
  }, [canManage]);
  const ruleValid = !!ruleDraft && ruleDraft.name.trim().length > 0 && ruleDraft.name.length <= 150 && !controls.test(ruleDraft.name) && conditions.includes(ruleDraft.condition as typeof conditions[number]) && integer(ruleDraft.threshold, 1, 1000) && integer(ruleDraft.windowSeconds, 1, 2592000) && integer(ruleDraft.graceSeconds, 0, 2592000) && integer(ruleDraft.reminderSeconds, 0, 2592000) && (!(["LongRunning", "WorkerUnavailable"].includes(ruleDraft.condition)) || ruleDraft.threshold === 1) && sinks.some(s => s.id === ruleDraft.sinkId && s.enabled);
  const sinkValid = !!sinkDraft && sinkDraft.name.trim().length > 0 && sinkDraft.name.length <= 150 && !controls.test(sinkDraft.name) && sinkDraft.type === "webhook" && validUrl(sinkDraft.configuration.url) && validHeaders(sinkDraft.configuration.headers) && integer(sinkDraft.maxAttempts, 1, 20) && integer(sinkDraft.retryBaseDelaySeconds, 0, 86400) && integer(sinkDraft.retryMaxDelaySeconds, sinkDraft.retryBaseDelaySeconds, 86400) && integer(sinkDraft.timeoutSeconds, 1, 120) && new TextEncoder().encode(JSON.stringify(sinkDraft.configuration)).length <= 4000;
  const saveRule = async () => {
    setInvalid(true); setSaved(false); if (!ruleDraft || !ruleEtag || !ruleValid) return;
    const generation = requestGeneration.current.rule;
    setBusy(true); setError("");
    try { const result = await mutate<{ rule: Omit<AlertRule, "version">; version: string }>(`/api/alert-rules/${ruleDraft.id}`, "PUT", { id: ruleDraft.id, name: ruleDraft.name.trim(), condition: ruleDraft.condition, threshold: ruleDraft.threshold, windowSeconds: ruleDraft.windowSeconds, graceSeconds: ruleDraft.graceSeconds, reminderSeconds: ruleDraft.reminderSeconds, sinkId: ruleDraft.sinkId, enabled: ruleDraft.enabled }, ruleEtag); const updated = { ...result.rule, version: result.version }; if (generation !== requestGeneration.current.rule) return; setRules(items => items.map(item => item.id === updated.id ? updated : item)); setRuleDraft(updated); setRuleEtag(`"${updated.version}"`); setCreatedSink(false); setSaved(true); setInvalid(false); setRuleDirty(false); }
    catch (cause) { if (generation === requestGeneration.current.rule) setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); } finally { setBusy(false); }
  };
  const createRule = async () => {
    setInvalid(true); setSaved(false); if (!ruleDraft || !ruleValid) return;
    const generation = requestGeneration.current.rule;
    setBusy(true); setError("");
    try {
      try { await mutate(`/api/alert-rules/${ruleDraft.id}`, "PUT", rulePayload({ ...ruleDraft, name: ruleDraft.name.trim(), enabled: false })); }
      catch (cause) {
        if (!(cause instanceof ApiError) || cause.status !== 412) throw cause;
        const { data, etag } = await read<AlertRule>(`/api/alert-rules/${ruleDraft.id}`);
        if (generation !== requestGeneration.current.rule) return;
        setRuleDraft(data); setRuleEtag(etag ?? undefined); setRules(items => [data, ...items.filter(item => item.id !== data.id)]);
        setCreatingRule(false); setRuleDirty(false); setSaved(true); setInvalid(false); return;
      }
      const { data, etag } = await read<AlertRule>(`/api/alert-rules/${ruleDraft.id}`);
      if (generation !== requestGeneration.current.rule) return;
      setRuleDraft(data); setRuleEtag(etag ?? undefined); setRules(items => [data, ...items.filter(item => item.id !== data.id)]);
      setCreatingRule(false); setSaved(true); setInvalid(false); setRuleDirty(false);
    } catch (cause) { if (generation === requestGeneration.current.rule) setError(t.alertRuleCreateError); }
    finally { setBusy(false); }
  };
  const enableRule = async () => {
    if (!ruleDraft || !ruleEtag || ruleDraft.enabled) return;
    const generation = requestGeneration.current.rule;
    setBusy(true); setError("");
    try {
      const result = await mutate<{ rule: Omit<AlertRule, "version">; version: string }>(`/api/alert-rules/${ruleDraft.id}`, "PUT", rulePayload({ ...ruleDraft, enabled: true }), ruleEtag);
      if (generation !== requestGeneration.current.rule) return;
      const updated = { ...result.rule, version: result.version }; setRules(items => items.map(item => item.id === updated.id ? updated : item));
      setRuleDraft(updated); setRuleEtag(`"${updated.version}"`); setRuleDirty(false); setSaved(true);
    } catch (cause) { if (generation === requestGeneration.current.rule) setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); }
    finally { setBusy(false); }
  };
  const saveSink = async () => {
    setInvalid(true); setSaved(false); if (!sinkDraft || (!creatingSink && !sinkEtag) || !sinkValid) return;
    const generation = requestGeneration.current.sink;
    setBusy(true); setError("");
    const configuration = { url: sinkDraft.configuration.url.trim(), headers: sinkDraft.configuration.headers.map(({ name, secretReference }) => ({ name: name.trim(), secretReference: secretReference.trim() })) };
    try {
      try { await mutate<{ definition: unknown; version: string }>(`/api/alert-sinks/${sinkDraft.id}`, "PUT", sinkPayload({ ...sinkDraft, name: sinkDraft.name.trim(), configuration }), creatingSink ? undefined : sinkEtag); }
      catch (cause) {
        if (!creatingSink || !(cause instanceof ApiError) || cause.status !== 412) throw cause;
        const { data, etag } = await read<AlertSink>(`/api/alert-sinks/${sinkDraft.id}`);
        const clean = safeSink(data);
        if (generation !== requestGeneration.current.sink) return;
        setSinkDraft(clean); setSinkEtag(etag ?? undefined); setSinks(items => [clean, ...items.filter(item => item.id !== clean.id)]);
        setCreatingSink(false); setSinkDirty(false); setCreatedSink(true); setSaved(true); setInvalid(false); return;
      }
      const { data, etag } = await read<AlertSink>(`/api/alert-sinks/${sinkDraft.id}`);
      const clean = safeSink(data);
      if (generation !== requestGeneration.current.sink) return;
      setSinkDraft(clean); setSinkEtag(etag ?? undefined); setSinks(items => items.some(item => item.id === clean.id) ? items.map(item => item.id === clean.id ? clean : item) : [clean, ...items]); setCreatingSink(false); setCreatedSink(creatingSink); setSaved(true); setInvalid(false); setSinkDirty(false);
    }
    catch (cause) {
      if (generation === requestGeneration.current.sink) setError(creatingSink ? t.alertSinkCreateError : cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError);
    } finally { setBusy(false); }
  };
  const updateRule = (patch: Partial<AlertRule>) => { setRuleDirty(true); setRuleDraft(current => current ? { ...current, ...patch } : current); };
  const updateSink = (patch: Partial<AlertSink>) => { setSinkDirty(true); setSinkDraft(current => current ? { ...current, ...patch } : current); };
  const updateHeader = (index: number, patch: Partial<HeaderReference>) => { if (!sinkDraft) return; updateSink({ configuration: { ...sinkDraft.configuration, headers: sinkDraft.configuration.headers.map((h, i) => i === index ? { ...h, ...patch } : h) } }); };
  const date = (value: string, pt: boolean) => new Intl.DateTimeFormat(pt ? "pt-BR" : "en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const pt = t.settings === "Configurações";

  return <Stack spacing={3}>
    {error && <Alert severity="error" action={<Button color="inherit" onClick={() => void load()}>{t.reload}</Button>}>{error}</Alert>}
    <Dialog open={!!pendingSelection} onClose={() => setPendingSelection(undefined)} aria-labelledby="alert-discard-title" aria-describedby="alert-discard-description">
      <DialogTitle id="alert-discard-title">{t.alertDiscardTitle}</DialogTitle>
      <DialogContent><DialogContentText id="alert-discard-description">{t.alertDiscardMessage}</DialogContentText></DialogContent>
      <DialogActions><Button onClick={() => setPendingSelection(undefined)}>{t.cancel}</Button><Button color="error" onClick={() => { if (pendingSelection) applySelection(pendingSelection.kind, pendingSelection.id); }}>{t.alertDiscardConfirm}</Button></DialogActions>
    </Dialog>
    {saved && <Alert severity="success">{createdSink ? t.alertSinkCreated : t.saved}</Alert>}
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Typography component="h2" variant="h6" sx={{ mb: 2 }}>{t.alertIncidents}</Typography>
      <Table size="small" aria-label={t.alertIncidents}><TableHead><TableRow>{[t.alertRule, t.alertCondition, t.alertSubject, t.alertState, t.alertLastObserved, t.alertCode].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody>{incidents.map(i => <TableRow key={i.incidentId}><TableCell>{i.ruleName}</TableCell><TableCell>{i.condition}</TableCell><TableCell>{i.subjectKey}</TableCell><TableCell>{i.state}</TableCell><TableCell>{date(i.lastObservedAtUtc, pt)}</TableCell><TableCell>{i.safeCode ?? "—"}</TableCell></TableRow>)}{!incidents.length && <TableRow><TableCell colSpan={6}>{t.alertNoIncidents}</TableCell></TableRow>}</TableBody></Table>
    </Paper>
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Typography component="h2" variant="h6" sx={{ mb: 2 }}>{t.alertDeliveries}</Typography>
      <Table size="small" aria-label={t.alertDeliveries}><TableHead><TableRow>{[t.alertDelivery, t.alertKind, t.alertState, t.alertAttempts, t.alertNextAttempt, t.alertCode].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody>{deliveries.map(d => <TableRow key={d.deliveryId}><TableCell>{d.deliveryId}</TableCell><TableCell>{d.kind}</TableCell><TableCell>{d.status}</TableCell><TableCell>{d.attemptCount}</TableCell><TableCell>{date(d.nextAvailableAtUtc, pt)}</TableCell><TableCell>{d.safeCode ?? "—"}</TableCell></TableRow>)}{!deliveries.length && <TableRow><TableCell colSpan={6}>{t.alertNoDeliveries}</TableCell></TableRow>}</TableBody></Table>
    </Paper>
    {canManage && <>
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 900 }}><Stack spacing={2}>
        <Typography component="h2" variant="h6">{t.alertRules}</Typography><Typography color="text.secondary">{t.alertDuplicateDeliveryHelp}</Typography>
        <Button variant="outlined" disabled={!sinksLoaded} onClick={() => requestSelection("rule", undefined)}>{t.alertNewRule}</Button>
        {sinksLoaded && !sinks.some(s => s.enabled) && <Typography role="status" color="text.secondary">{t.alertRuleNeedsSink}</Typography>}
        <TextField select label={t.alertRule} value={ruleId} onChange={e => requestSelection("rule", e.target.value)} disabled={!rules.length}>{rules.map(r => <MenuItem key={r.id} value={r.id}>{r.name} · {r.enabled ? t.enabled : t.disabled}</MenuItem>)}</TextField>
        {ruleDraft && <Stack spacing={2}>
          <TextField label={t.alertRuleName} value={ruleDraft.name} onChange={e => updateRule({ name: e.target.value })} />
          <TextField select label={t.alertCondition} value={ruleDraft.condition} onChange={e => updateRule({ condition: e.target.value })}>{conditions.map(c => <MenuItem key={c} value={c}>{t[`alertCondition${c}` as keyof Labels]}</MenuItem>)}</TextField>
          <TextField type="number" label={t.alertThreshold} value={ruleDraft.threshold} error={invalid && (!integer(ruleDraft.threshold, 1, 1000) || (["LongRunning", "WorkerUnavailable"].includes(ruleDraft.condition) && ruleDraft.threshold !== 1))} onChange={e => updateRule({ threshold: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 1000 } }} />
          <TextField type="number" label={t.alertWindowSeconds} value={ruleDraft.windowSeconds} error={invalid && !integer(ruleDraft.windowSeconds, 1, 2592000)} onChange={e => updateRule({ windowSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 2592000 } }} />
          <TextField type="number" label={t.alertGraceSeconds} value={ruleDraft.graceSeconds} error={invalid && !integer(ruleDraft.graceSeconds, 0, 2592000)} onChange={e => updateRule({ graceSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 2592000 } }} />
          <TextField type="number" label={t.alertReminderSeconds} value={ruleDraft.reminderSeconds} error={invalid && !integer(ruleDraft.reminderSeconds, 0, 2592000)} onChange={e => updateRule({ reminderSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 2592000 } }} />
          <TextField select label={t.alertSink} value={ruleDraft.sinkId} onChange={e => updateRule({ sinkId: e.target.value })}>{sinks.map(s => <MenuItem key={s.id} value={s.id} disabled={!s.enabled}>{s.name}{s.enabled ? "" : ` · ${t.disabled}`}</MenuItem>)}</TextField>
          {!ruleDraft.enabled && !creatingRule && <Button onClick={enableRule} disabled={busy || !ruleEtag || !ruleValid}>{t.alertEnableRule}</Button>}
          {invalid && !ruleValid && <Typography role="alert" color="error">{t.validation}</Typography>}
          {invalid && (!integer(ruleDraft.threshold, 1, 1000) || (["LongRunning", "WorkerUnavailable"].includes(ruleDraft.condition) && ruleDraft.threshold !== 1)) && <Typography role="alert" color="error">{t.alertRuleThresholdInvalid}</Typography>}
          {invalid && !integer(ruleDraft.windowSeconds, 1, 2592000) && <Typography role="alert" color="error">{t.alertRuleWindowInvalid}</Typography>}
          {invalid && !integer(ruleDraft.graceSeconds, 0, 2592000) && <Typography role="alert" color="error">{t.alertRuleGraceInvalid}</Typography>}
          {invalid && !integer(ruleDraft.reminderSeconds, 0, 2592000) && <Typography role="alert" color="error">{t.alertRuleReminderInvalid}</Typography>}
          {creatingRule ? <Button variant="contained" onClick={createRule} disabled={busy || !sinks.some(s => s.enabled)}>{t.alertCreateRule}</Button> : <Button variant="contained" onClick={saveRule} disabled={busy || !ruleEtag}>{t.save}</Button>}
        </Stack>}
      </Stack></Paper>
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 900 }}><Stack spacing={2}>
        <Typography component="h2" variant="h6">{t.alertWebhookSinks}</Typography><Typography color="text.secondary">{t.alertSecretReferenceHelp}</Typography>
        <Button variant="outlined" onClick={() => requestSelection("sink", undefined)}>{t.alertNewWebhookSink}</Button>
        <TextField select label={t.alertSink} value={sinkId} onChange={e => requestSelection("sink", e.target.value)} disabled={!sinks.length}>{sinks.map(s => <MenuItem key={s.id} value={s.id}>{s.name} · {s.enabled ? t.enabled : t.disabled}</MenuItem>)}</TextField>
        {sinkDraft && <Stack spacing={2}>
          <TextField label={t.alertSinkName} value={sinkDraft.name} onChange={e => updateSink({ name: e.target.value })} />
          <TextField label={t.alertWebhookUrl} value={sinkDraft.configuration.url} onChange={e => updateSink({ configuration: { ...sinkDraft.configuration, url: e.target.value } })} error={invalid && !validUrl(sinkDraft.configuration.url)} />
          <Typography variant="subtitle2">{t.alertCustomHeaders}</Typography>
          {sinkDraft.configuration.headers.map((h, i) => <Stack key={i} direction={{ xs: "column", sm: "row" }} spacing={1}><TextField label={t.alertHeaderName} value={h.name} onChange={e => updateHeader(i, { name: e.target.value })} error={invalid && (!validHeaderName(h.name) || sinkDraft.configuration.headers.some((other, j) => j !== i && other.name.toLowerCase() === h.name.toLowerCase()))} /><TextField label={t.alertSecretReference} value={h.secretReference} onChange={e => updateHeader(i, { secretReference: e.target.value })} error={invalid && !validSecretReference(h.secretReference)} /><Button onClick={() => updateSink({ configuration: { ...sinkDraft.configuration, headers: sinkDraft.configuration.headers.filter((_, j) => j !== i) } })}>{t.remove}</Button></Stack>)}
          <Button onClick={() => updateSink({ configuration: { ...sinkDraft.configuration, headers: [...sinkDraft.configuration.headers, { name: "", secretReference: "" }] } })} disabled={sinkDraft.configuration.headers.length >= 32}>{t.alertAddHeader}</Button>
          <TextField type="number" label={t.maxAttempts} value={sinkDraft.maxAttempts} onChange={e => updateSink({ maxAttempts: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 20 } }} />
          <TextField type="number" label={t.retryBaseDelay} value={sinkDraft.retryBaseDelaySeconds} onChange={e => updateSink({ retryBaseDelaySeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 86400 } }} />
          <TextField type="number" label={t.retryMaxDelay} value={sinkDraft.retryMaxDelaySeconds} onChange={e => updateSink({ retryMaxDelaySeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 86400 } }} />
          <TextField type="number" label={t.timeout} value={sinkDraft.timeoutSeconds} onChange={e => updateSink({ timeoutSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 120 } }} />
          <FormControlLabel control={<Checkbox checked={sinkDraft.enabled} onChange={e => updateSink({ enabled: e.target.checked })} />} label={t.enabled} />
          {invalid && !sinkValid && <Typography role="alert" color="error">{t.validation}</Typography>}
          <Button variant="contained" onClick={saveSink} disabled={busy || (!creatingSink && !sinkEtag)}>{t.save}</Button>
        </Stack>}
      </Stack></Paper>
    </>}
  </Stack>;
}
