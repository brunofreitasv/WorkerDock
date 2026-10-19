import { useEffect, useState } from "react";
import { Alert, Button, Checkbox, FormControlLabel, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { ApiError, mutate, read } from "../../api/client";
import type { Labels } from "../../../../../packages/ui/src/preferences";

const conditions = ["RepeatedFailure", "RetryExhausted", "Timeout", "LongRunning", "MissedExecution", "WorkerUnavailable"] as const;
const forbiddenHeaders = new Set(["host", "content-length", "transfer-encoding", "connection", "keep-alive", "proxy-connection", "upgrade", "te", "trailer", "expect"]);
const controls = /[\u0000-\u001f\u007f]/;
interface AlertRule { id: string; name: string; condition: string; threshold: number; windowSeconds: number; graceSeconds: number; reminderSeconds: number; sinkId: string; enabled: boolean; version: string }
interface HeaderReference { name: string; secretReference: string }
interface AlertSink { id: string; name: string; type: string; configuration: { url: string; headers: HeaderReference[] }; maxAttempts: number; retryBaseDelaySeconds: number; retryMaxDelaySeconds: number; timeoutSeconds: number; enabled: boolean; version: string }
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
function validHeaders(headers: HeaderReference[]) {
  return headers.length <= 32 && headers.every(h => /^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$/.test(h.name) && !forbiddenHeaders.has(h.name.toLowerCase()) && h.secretReference.trim().length > 0 && h.secretReference.length <= 200 && !controls.test(h.secretReference)) && new Set(headers.map(h => h.name.toLowerCase())).size === headers.length;
}

export function AlertRulesPage({ t, canManage }: { t: Labels; canManage: boolean }) {
  const [rules, setRules] = useState<AlertRule[]>([]); const [sinks, setSinks] = useState<AlertSink[]>([]);
  const [ruleId, setRuleId] = useState(""); const [sinkId, setSinkId] = useState("");
  const [ruleDraft, setRuleDraft] = useState<AlertRule>(); const [sinkDraft, setSinkDraft] = useState<AlertSink>();
  const [ruleEtag, setRuleEtag] = useState<string>(); const [sinkEtag, setSinkEtag] = useState<string>();
  const [incidents, setIncidents] = useState<Incident[]>([]); const [deliveries, setDeliveries] = useState<Delivery[]>([]);
  const [error, setError] = useState(""); const [saved, setSaved] = useState(false); const [busy, setBusy] = useState(false); const [invalid, setInvalid] = useState(false);
  const load = () => {
    setError("");
    const tasks: Promise<unknown>[] = [read<Incident[]>("/api/alerts/incidents?limit=100").then(x => setIncidents(x.data)), read<Delivery[]>("/api/alerts/deliveries?limit=100").then(x => setDeliveries(x.data))];
    if (canManage) {
      tasks.push(read<AlertRule[]>("/api/alert-rules").then(x => { setRules(x.data); setRuleId(id => id || x.data[0]?.id || ""); }), read<AlertSink[]>("/api/alert-sinks").then(x => { const data = x.data.map(safeSink); setSinks(data); setSinkId(id => id || data[0]?.id || ""); }));
      if (ruleId) tasks.push(read<AlertRule>(`/api/alert-rules/${ruleId}`).then(({ data, etag }) => { setRuleDraft(data); setRuleEtag(etag ?? undefined); }));
      if (sinkId) tasks.push(read<AlertSink>(`/api/alert-sinks/${sinkId}`).then(({ data, etag }) => { setSinkDraft(safeSink(data)); setSinkEtag(etag ?? undefined); }));
    }
    void Promise.all(tasks).catch(() => setError(t.requestError));
  };
  useEffect(load, [canManage, t.requestError]);
  useEffect(() => { if (canManage && ruleId) void read<AlertRule>(`/api/alert-rules/${ruleId}`).then(({ data, etag }) => { setRuleDraft(data); setRuleEtag(etag ?? undefined); }).catch(() => setError(t.requestError)); }, [canManage, ruleId]);
  useEffect(() => { if (canManage && sinkId) void read<AlertSink>(`/api/alert-sinks/${sinkId}`).then(({ data, etag }) => { setSinkDraft(safeSink(data)); setSinkEtag(etag ?? undefined); }).catch(() => setError(t.requestError)); }, [canManage, sinkId]);
  const ruleValid = !!ruleDraft && ruleDraft.name.trim().length > 0 && ruleDraft.name.length <= 150 && !controls.test(ruleDraft.name) && conditions.includes(ruleDraft.condition as typeof conditions[number]) && integer(ruleDraft.threshold, 1, 1000) && integer(ruleDraft.windowSeconds, 1, 2592000) && integer(ruleDraft.graceSeconds, 0, 2592000) && integer(ruleDraft.reminderSeconds, 0, 2592000) && (!(["LongRunning", "WorkerUnavailable"].includes(ruleDraft.condition)) || ruleDraft.threshold === 1) && sinks.some(s => s.id === ruleDraft.sinkId && s.enabled);
  const sinkValid = !!sinkDraft && sinkDraft.name.trim().length > 0 && sinkDraft.name.length <= 150 && !controls.test(sinkDraft.name) && sinkDraft.type === "webhook" && validUrl(sinkDraft.configuration.url) && validHeaders(sinkDraft.configuration.headers) && integer(sinkDraft.maxAttempts, 1, 20) && integer(sinkDraft.retryBaseDelaySeconds, 0, 86400) && integer(sinkDraft.retryMaxDelaySeconds, sinkDraft.retryBaseDelaySeconds, 86400) && integer(sinkDraft.timeoutSeconds, 1, 120) && new TextEncoder().encode(JSON.stringify(sinkDraft.configuration)).length <= 4000;
  const saveRule = async () => {
    setInvalid(true); setSaved(false); if (!ruleDraft || !ruleEtag || !ruleValid) return;
    setBusy(true); setError("");
    try { const result = await mutate<{ rule: Omit<AlertRule, "version">; version: string }>(`/api/alert-rules/${ruleDraft.id}`, "PUT", { id: ruleDraft.id, name: ruleDraft.name.trim(), condition: ruleDraft.condition, threshold: ruleDraft.threshold, windowSeconds: ruleDraft.windowSeconds, graceSeconds: ruleDraft.graceSeconds, reminderSeconds: ruleDraft.reminderSeconds, sinkId: ruleDraft.sinkId, enabled: ruleDraft.enabled }, ruleEtag); const updated = { ...result.rule, version: result.version }; setRuleDraft(updated); setRuleEtag(`"${updated.version}"`); setRules(items => items.map(item => item.id === updated.id ? updated : item)); setSaved(true); setInvalid(false); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); } finally { setBusy(false); }
  };
  const saveSink = async () => {
    setInvalid(true); setSaved(false); if (!sinkDraft || !sinkEtag || !sinkValid) return;
    setBusy(true); setError("");
    const configuration = { url: sinkDraft.configuration.url.trim(), headers: sinkDraft.configuration.headers.map(({ name, secretReference }) => ({ name: name.trim(), secretReference: secretReference.trim() })) };
    try { const result = await mutate<{ definition: unknown; version: string }>(`/api/alert-sinks/${sinkDraft.id}`, "PUT", { id: sinkDraft.id, name: sinkDraft.name.trim(), type: "webhook", configuration, maxAttempts: sinkDraft.maxAttempts, retryBaseDelaySeconds: sinkDraft.retryBaseDelaySeconds, retryMaxDelaySeconds: sinkDraft.retryMaxDelaySeconds, timeoutSeconds: sinkDraft.timeoutSeconds, enabled: sinkDraft.enabled }, sinkEtag); const clean = safeSink({ ...sinkDraft, name: sinkDraft.name.trim(), configuration, version: result.version }); setSinkDraft(clean); setSinkEtag(`"${result.version}"`); setSinks(items => items.map(item => item.id === clean.id ? clean : item)); setSaved(true); setInvalid(false); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.stale : t.requestError); } finally { setBusy(false); }
  };
  const updateRule = (patch: Partial<AlertRule>) => setRuleDraft(current => current ? { ...current, ...patch } : current);
  const updateSink = (patch: Partial<AlertSink>) => setSinkDraft(current => current ? { ...current, ...patch } : current);
  const updateHeader = (index: number, patch: Partial<HeaderReference>) => { if (!sinkDraft) return; updateSink({ configuration: { ...sinkDraft.configuration, headers: sinkDraft.configuration.headers.map((h, i) => i === index ? { ...h, ...patch } : h) } }); };
  const date = (value: string, pt: boolean) => new Intl.DateTimeFormat(pt ? "pt-BR" : "en", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const pt = t.settings === "Configurações";

  return <Stack spacing={3}>
    {error && <Alert severity="error" action={<Button color="inherit" onClick={load}>{t.reload}</Button>}>{error}</Alert>}
    {saved && <Alert severity="success">{t.saved}</Alert>}
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
        <TextField select label={t.alertRule} value={ruleId} onChange={e => { setRuleId(e.target.value); setRuleDraft(undefined); setRuleEtag(undefined); }} disabled={!rules.length}>{rules.map(r => <MenuItem key={r.id} value={r.id}>{r.name} · {r.enabled ? t.enabled : t.disabled}</MenuItem>)}</TextField>
        {ruleDraft && <Stack spacing={2}>
          <TextField label={t.alertRuleName} value={ruleDraft.name} onChange={e => updateRule({ name: e.target.value })} />
          <TextField select label={t.alertCondition} value={ruleDraft.condition} onChange={e => updateRule({ condition: e.target.value })}>{conditions.map(c => <MenuItem key={c} value={c}>{t[`alertCondition${c}` as keyof Labels]}</MenuItem>)}</TextField>
          <TextField type="number" label={t.alertThreshold} value={ruleDraft.threshold} onChange={e => updateRule({ threshold: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 1000 } }} />
          <TextField type="number" label={t.alertWindowSeconds} value={ruleDraft.windowSeconds} onChange={e => updateRule({ windowSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 2592000 } }} />
          <TextField type="number" label={t.alertGraceSeconds} value={ruleDraft.graceSeconds} onChange={e => updateRule({ graceSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 2592000 } }} />
          <TextField type="number" label={t.alertReminderSeconds} value={ruleDraft.reminderSeconds} onChange={e => updateRule({ reminderSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 2592000 } }} />
          <TextField select label={t.alertSink} value={ruleDraft.sinkId} onChange={e => updateRule({ sinkId: e.target.value })}>{sinks.map(s => <MenuItem key={s.id} value={s.id} disabled={!s.enabled}>{s.name}{s.enabled ? "" : ` · ${t.disabled}`}</MenuItem>)}</TextField>
          <FormControlLabel control={<Checkbox checked={ruleDraft.enabled} onChange={e => updateRule({ enabled: e.target.checked })} />} label={t.enabled} />
          {invalid && !ruleValid && <Typography role="alert" color="error">{t.validation}</Typography>}
          <Button variant="contained" onClick={saveRule} disabled={busy || !ruleEtag}>{t.save}</Button>
        </Stack>}
      </Stack></Paper>
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 900 }}><Stack spacing={2}>
        <Typography component="h2" variant="h6">{t.alertWebhookSinks}</Typography><Typography color="text.secondary">{t.alertSecretReferenceHelp}</Typography>
        <TextField select label={t.alertSink} value={sinkId} onChange={e => { setSinkId(e.target.value); setSinkDraft(undefined); setSinkEtag(undefined); }} disabled={!sinks.length}>{sinks.map(s => <MenuItem key={s.id} value={s.id}>{s.name} · {s.enabled ? t.enabled : t.disabled}</MenuItem>)}</TextField>
        {sinkDraft && <Stack spacing={2}>
          <TextField label={t.alertSinkName} value={sinkDraft.name} onChange={e => updateSink({ name: e.target.value })} />
          <TextField label={t.alertWebhookUrl} value={sinkDraft.configuration.url} onChange={e => updateSink({ configuration: { ...sinkDraft.configuration, url: e.target.value } })} error={invalid && !validUrl(sinkDraft.configuration.url)} />
          <Typography variant="subtitle2">{t.alertCustomHeaders}</Typography>
          {sinkDraft.configuration.headers.map((h, i) => <Stack key={i} direction={{ xs: "column", sm: "row" }} spacing={1}><TextField label={t.alertHeaderName} value={h.name} onChange={e => updateHeader(i, { name: e.target.value })} /><TextField label={t.alertSecretReference} value={h.secretReference} onChange={e => updateHeader(i, { secretReference: e.target.value })} /><Button onClick={() => updateSink({ configuration: { ...sinkDraft.configuration, headers: sinkDraft.configuration.headers.filter((_, j) => j !== i) } })}>{t.remove}</Button></Stack>)}
          <Button onClick={() => updateSink({ configuration: { ...sinkDraft.configuration, headers: [...sinkDraft.configuration.headers, { name: "", secretReference: "" }] } })} disabled={sinkDraft.configuration.headers.length >= 32}>{t.alertAddHeader}</Button>
          <TextField type="number" label={t.maxAttempts} value={sinkDraft.maxAttempts} onChange={e => updateSink({ maxAttempts: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 20 } }} />
          <TextField type="number" label={t.retryBaseDelay} value={sinkDraft.retryBaseDelaySeconds} onChange={e => updateSink({ retryBaseDelaySeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 86400 } }} />
          <TextField type="number" label={t.retryMaxDelay} value={sinkDraft.retryMaxDelaySeconds} onChange={e => updateSink({ retryMaxDelaySeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 0, max: 86400 } }} />
          <TextField type="number" label={t.timeout} value={sinkDraft.timeoutSeconds} onChange={e => updateSink({ timeoutSeconds: Number(e.target.value) })} slotProps={{ htmlInput: { min: 1, max: 120 } }} />
          <FormControlLabel control={<Checkbox checked={sinkDraft.enabled} onChange={e => updateSink({ enabled: e.target.checked })} />} label={t.enabled} />
          {invalid && !sinkValid && <Typography role="alert" color="error">{t.validation}</Typography>}
          <Button variant="contained" onClick={saveSink} disabled={busy || !sinkEtag}>{t.save}</Button>
        </Stack>}
      </Stack></Paper>
    </>}
  </Stack>;
}
