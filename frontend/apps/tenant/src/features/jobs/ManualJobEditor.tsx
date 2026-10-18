import { useEffect, useMemo, useState } from "react";
import { Alert, Button, CircularProgress, Paper, Stack, TextField, Typography } from "@mui/material";
import { TemplateForm, validateSchema, type Labels } from "@workerdock/ui";
import { ApiError, mutate, read } from "../../api/client";
import { TemplatePicker, type CatalogItem } from "../templates/TemplatePicker";

type Issue = { path: string; code: string };
type JobRevision = { jobId: string; revision: number; name: string; templateId: string; templateVersion: string; configuration: Record<string, unknown>; profileId: string | null; policy: Record<string, unknown> };
type EditorDto = { revision: JobRevision; version: number[]; enabled: boolean; archived: boolean; concurrencyLimit: number };
const blankPolicy = { timeout: null, maxAttempts: 1, retryBaseDelay: "00:00:01", retryMaxDelay: "00:00:30", jitterRatio: 0, retryableCodes: [], retryUncertain: false, overlap: "Queue", backlogLimit: 100, priority: 50 };

export function ManualJobEditor({ t }: { t: Labels }) {
  const [catalog, setCatalog] = useState<CatalogItem[]>([]); const [selected, setSelected] = useState<CatalogItem>();
  const [jobId, setJobId] = useState(""); const [etag, setEtag] = useState<string>(); const [name, setName] = useState(""); const [configuration, setConfiguration] = useState<Record<string, unknown>>({});
  const [enabled, setEnabled] = useState(false); const [issues, setIssues] = useState<Issue[]>([]); const [validated, setValidated] = useState(false); const [message, setMessage] = useState(""); const [busy, setBusy] = useState(false); const [loading, setLoading] = useState(true);
  useEffect(() => { let active = true; void read<{ items: CatalogItem[] }>("/api/templates").then(({ data }) => { if (active) { setCatalog(data.items); setSelected(data.items.find((item) => item.selectable)); } }).catch(() => setMessage(t.requestError)).finally(() => { if (active) setLoading(false); }); return () => { active = false; }; }, [t.requestError]);
  const localIssues = useMemo(() => selected ? validateSchema(selected.descriptor.configurationSchema, configuration) : [], [selected, configuration]);
  const changeConfiguration = (value: Record<string, unknown>) => { setConfiguration(value); setIssues([]); setValidated(false); setEnabled(false); };
  const selectTemplate = (item: CatalogItem) => { setSelected(item); setConfiguration(defaults(item.descriptor.configurationSchema)); setIssues([]); setValidated(false); setEnabled(false); setMessage(""); };
  const saveDraft = async () => {
    if (!selected || !name.trim()) { setMessage(t.jobNameRequired); return; }
    setBusy(true); setMessage("");
    const body: JobRevision = { jobId: jobId || "00000000-0000-0000-0000-000000000000", revision: 0, name: name.trim(), templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration, profileId: typeof configuration.profileId === "string" && configuration.profileId ? configuration.profileId : null, policy: blankPolicy };
    try {
      const result = jobId ? await mutate<{ commandId: string }>(`/api/jobs/${jobId}`, "PUT", body, etag) : await mutate<{ targetId: string }>("/api/jobs", "POST", body);
      const savedId = jobId || ("targetId" in result ? result.targetId : "");
      if (!savedId) throw new Error("JobIdUnavailable");
      const current = await read<EditorDto>(`/api/jobs/${savedId}/configuration`);
      setJobId(savedId); setEtag(current.etag ?? undefined); setEnabled(current.data.enabled); setMessage(t.draftSaved);
    } catch (error) { setMessage(error instanceof ApiError && error.status === 412 ? t.stalePreserve : t.requestError); }
    finally { setBusy(false); }
  };
  const validate = async () => {
    if (!selected) return;
    setBusy(true); setMessage("");
    try { const result = await mutate<{ issues: Issue[] }>("/api/template-validation", "POST", { templateId: selected.descriptor.id, templateVersion: selected.descriptor.version, configuration }); setIssues(result.issues); setValidated(result.issues.length === 0); setMessage(result.issues.length ? t.validation : t.configurationValid); }
    catch { setMessage(t.requestError); } finally { setBusy(false); }
  };
  const enable = async () => { if (!jobId || !etag || localIssues.length || issues.length) return; setBusy(true); try { const receipt = await mutate<{ commandId: string }>(`/api/jobs/${jobId}/enable-manual`, "POST", undefined, etag); const current = await read<EditorDto>(`/api/jobs/${jobId}/configuration`); setEtag(current.etag ?? undefined); setEnabled(current.data.enabled); setMessage(receipt.commandId ? t.manualEnabled : t.requestError); } catch (error) { setMessage(error instanceof ApiError && error.status === 412 ? t.stalePreserve : t.requestError); } finally { setBusy(false); } };
  const run = async () => { if (!jobId || !enabled) return; setBusy(true); try { const result = await mutate<{ targetId: string }>(`/api/jobs/${jobId}/runs`, "POST", { requestKey: crypto.randomUUID() }); setMessage(`${t.executionAccepted}: ${result.targetId}`); } catch (error) { setMessage(error instanceof ApiError && error.status === 412 ? t.stalePreserve : t.requestError); } finally { setBusy(false); } };
  if (loading) return <CircularProgress aria-label={t.loading} />;
  if (!catalog.length) return <Alert severity="info">{t.noTemplates}</Alert>;
  const errors = Object.fromEntries(issues.map((issue) => [issue.path, issue.code]));
  return <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 1000 }}><Stack spacing={3}>
    <Typography variant="h5">{t.manualJobs}</Typography>
    {message && <Alert severity={message === t.requestError || issues.length ? "error" : "info"} role="status">{message}</Alert>}
    <TextField label={t.jobName} value={name} onChange={(event) => setName(event.target.value)} required fullWidth />
    <TemplatePicker label={t.template} value={selected} onChange={selectTemplate} />
    {selected && <TemplateForm descriptor={selected.descriptor} value={configuration} onChange={changeConfiguration} errors={errors} />}
    {localIssues.length > 0 && <Alert severity="warning">{t.localValidation}: {localIssues.length}</Alert>}
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ flexWrap: "wrap" }}>
      <Button variant="outlined" disabled={busy || !selected} onClick={() => void validate()}>{t.validateConfiguration}</Button>
      <Button variant="contained" disabled={busy || !selected} onClick={() => void saveDraft()}>{t.saveDisabledDraft}</Button>
      <Button disabled={busy || !jobId || !validated || issues.length > 0 || localIssues.length > 0} onClick={() => void enable()}>{t.enableManual}</Button>
      <Button disabled={busy || !enabled} onClick={() => void run()}>{t.runNow}</Button>
    </Stack>
    {enabled && <Typography color="success.main">{t.manualJobEnabled}</Typography>}
  </Stack></Paper>;
}

function defaults(schema: any): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, property] of Object.entries<any>(schema.properties ?? {})) {
    if (Object.hasOwn(property, "default")) result[key] = property.default;
    else if (property.$ref?.startsWith("#/$defs/")) result[key] = "";
    else if (property.type === "object" || property.properties) result[key] = defaults(property);
    else if (property.type === "array") result[key] = [];
    else if (property.enum?.length) result[key] = property.enum[0];
    else if (Array.isArray(property.type) && property.type.includes("null")) result[key] = null;
    else if (property.type === "boolean") result[key] = false;
    else result[key] = "";
  }
  return result;
}
