import { useEffect, useMemo, useState } from "react";
import { Alert, Box, Button, FormControlLabel, Paper, Stack, Switch, TextField, Typography } from "@mui/material";
import { TemplateForm, type DynamicValuePlan, type Labels, type TemplateDescriptorDto } from "@workerdock/ui";
import { ApiError, mutate } from "../../api/client";
import { changedJobSections, createJobDefinitionDraft, type JobDefinitionDraft } from "./JobDefinitionDraft";
import { ScheduleEditor, type ScheduleEditorInputDto } from "../schedules/ScheduleEditor";
import type { SchedulePreviewItemDto } from "../schedules/SchedulePreview";

export function ManagedJobEditor({ jobId, initial, etag, descriptor, t, onSaved, onCancel, onDirtyChange }: { jobId: string; initial: JobDefinitionDraft; etag: string | null; descriptor: TemplateDescriptorDto; t: Labels; onSaved(): void; onCancel(): void; onDirtyChange?(dirty: boolean): void }) {
  const baseline = useMemo(() => createJobDefinitionDraft(initial), [initial]);
  const [draft, setDraft] = useState(() => createJobDefinitionDraft(initial));
  const [error, setError] = useState("");
  const [review, setReview] = useState(false);
  const [saving, setSaving] = useState(false);
  const [missedPolicyText, setMissedPolicyText] = useState(JSON.stringify(draft.missedPolicy, null, 2));
  const [executionPolicyText, setExecutionPolicyText] = useState(JSON.stringify(draft.revision.policy ?? {}, null, 2));
  const [invalidMissedPolicy, setInvalidMissedPolicy] = useState(false);
  const [invalidExecutionPolicy, setInvalidExecutionPolicy] = useState(false);
  const parseError = invalidMissedPolicy || invalidExecutionPolicy;
  const dirty = changedJobSections(baseline, draft).length > 0;
  useEffect(() => { onDirtyChange?.(dirty); return () => onDirtyChange?.(false); }, [dirty, onDirtyChange]);
  useEffect(() => { setMissedPolicyText(JSON.stringify(draft.missedPolicy, null, 2)); }, [draft.missedPolicy]);
  useEffect(() => { setExecutionPolicyText(JSON.stringify(draft.revision.policy ?? {}, null, 2)); }, [draft.revision.policy]);
  useEffect(() => {
    if (!dirty) return;
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ""; };
    window.addEventListener("beforeunload", beforeUnload);
    return () => window.removeEventListener("beforeunload", beforeUnload);
  }, [dirty]);
  const cancel = () => { if (!dirty || window.confirm(t.jobDiscardConfirm)) onCancel(); };
  const save = async () => {
    if (parseError) return;
    setSaving(true); setError("");
    try {
      if (!etag) throw new Error("ETagUnavailable");
      await mutate(`/api/jobs/${jobId}/definition`, "PUT", draft, etag);
      onSaved();
    } catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? t.staleDraftPreserved : t.actionError); }
    finally { setSaving(false); }
  };
  return <Paper component="section" aria-label={t.jobEditTitle} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 980 }}><Stack spacing={3}>
    <Typography component="h2" variant="h6">{t.jobEditTitle}</Typography>
    {error && <Alert severity="error">{error}</Alert>}
    <Box component="section" aria-label={t.jobSectionConfiguration} sx={{ display: "grid", gap: 2 }}>
      <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{t.jobSectionConfiguration}</Typography>
    <TextField label={t.jobName} value={String(draft.revision.name ?? "")} onChange={event => setDraft({ ...draft, revision: { ...draft.revision, name: event.target.value } })} />
    <FormControlLabel control={<Switch checked={draft.revision.hideFailureDetails === true} onChange={event => setDraft({ ...draft, revision: { ...draft.revision, hideFailureDetails: event.target.checked } })} />} label={t.hideFailureDetails} />
    <Typography variant="body2">{t.hideFailureDetailsHelp}</Typography>
    <TemplateForm descriptor={descriptor} value={(draft.revision.configuration ?? {}) as Record<string, unknown>} onChange={configuration => setDraft({ ...draft, revision: { ...draft.revision, configuration } })} errors={{}} dynamicValues={(draft.revision.dynamicValues ?? null) as DynamicValuePlan | null} onDynamicValuesChange={dynamicValues => setDraft({ ...draft, revision: { ...draft.revision, dynamicValues } })} t={t} jobTimeout={typeof (draft.revision.policy as Record<string, unknown> | undefined)?.timeout === "string" ? (draft.revision.policy as Record<string, string>).timeout : null} />
    </Box>
    <Box component="section" aria-label={t.jobSectionSchedule} sx={{ display: "grid", gap: 2 }}>
      <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{t.jobSectionSchedule}</Typography>
    {draft.schedule ? <ScheduleEditor value={draft.schedule as unknown as ScheduleEditorInputDto} t={t} onChange={schedule => setDraft({ ...draft, schedule: schedule as unknown as Record<string, unknown> })} preview={async (input): Promise<SchedulePreviewItemDto[]> => mutate("/api/schedules/preview", "POST", { input, afterUtc: new Date().toISOString(), count: 5 })} /> : <Button onClick={() => setDraft({ ...draft, schedule: { kind: "Cron", cron: "0 9 * * 1-5", zoneId: "UTC", localOnce: null, selectedOffset: null } })}>{t.jobAddSchedule}</Button>}
    {draft.schedule && <Button onClick={() => setDraft({ ...draft, schedule: null, missedPolicy: null })}>{t.jobRemoveSchedule}</Button>}
    </Box>
    <Box component="section" aria-label={t.jobSectionPolicy} sx={{ display: "grid", gap: 2 }}>
      <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{t.jobSectionPolicy}</Typography>
    <TextField label={t.jobExecutionPolicyAdvancedLabel} multiline minRows={4} value={executionPolicyText} onChange={event => { setExecutionPolicyText(event.target.value); try { const parsed = JSON.parse(event.target.value) as Record<string, unknown>; setDraft(current => ({ ...current, revision: { ...current.revision, policy: parsed } })); setInvalidExecutionPolicy(false); setError(""); } catch { setInvalidExecutionPolicy(true); setError(t.jobDefinitionJsonInvalid); } }} />
    <TextField label={t.jobPolicyAdvancedLabel} multiline minRows={4} value={missedPolicyText} onChange={event => { setMissedPolicyText(event.target.value); try { const parsed = JSON.parse(event.target.value) as Record<string, unknown> | null; setDraft(current => ({ ...current, missedPolicy: parsed })); setInvalidMissedPolicy(false); setError(""); } catch { setInvalidMissedPolicy(true); setError(t.jobDefinitionJsonInvalid); } }} />
    </Box>
    {review && <Alert severity="info">{t.jobReviewChanges}: {changedJobSections(baseline, draft).join(", ") || t.jobNoChanges}. {t.jobLifecyclePreserved}</Alert>}
    <Stack role="group" aria-label={t.jobReviewSave} direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}><Button onClick={cancel}>{t.cancel}</Button><Button disabled={!dirty || saving || parseError} onClick={() => setReview(true)}>{t.jobReviewSave}</Button>{review && <Button disabled={!dirty || saving || parseError} variant="contained" onClick={() => void save()}>{saving ? t.loading : t.save}</Button>}</Stack>
  </Stack></Paper>;
}
