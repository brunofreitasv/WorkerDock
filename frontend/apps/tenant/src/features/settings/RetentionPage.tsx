import { useEffect, useState, type FormEvent } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  CircularProgress,
  FormControlLabel,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { ApiError, mutate, read } from '../../api/client';
import type { Language } from '@workerdock/ui';

type RetentionPolicy = {
  logDays: number;
  executionDays: number;
  auditDays: number;
  archiveBeforeDelete: boolean;
  archiveDays: number;
};

const defaults: RetentionPolicy = {
  logDays: 30,
  executionDays: 90,
  auditDays: 365,
  archiveBeforeDelete: false,
  archiveDays: 0,
};

const fields: Array<{ key: 'logDays' | 'executionDays' | 'auditDays'; label: string; help: string }> = [
  { key: 'logDays', label: 'Execution logs', help: 'Safe execution log records to keep.' },
  { key: 'executionDays', label: 'Execution history', help: 'Completed execution and attempt history to keep.' },
  { key: 'auditDays', label: 'Audit history', help: 'Audit records to keep. Purges are recorded in a purge summary.' },
];

function readPolicy(value: unknown): RetentionPolicy {
  if (!value || typeof value !== 'object') return defaults;
  const candidate = value as Partial<RetentionPolicy>;
  return {
    logDays: candidate.logDays ?? defaults.logDays,
    executionDays: candidate.executionDays ?? defaults.executionDays,
    auditDays: candidate.auditDays ?? defaults.auditDays,
    archiveBeforeDelete: candidate.archiveBeforeDelete ?? false,
    archiveDays: candidate.archiveDays ?? defaults.archiveDays,
  };
}
export default function RetentionPage({ language = 'en' }: { language?: Language }) {
  const sectionCopy = language === 'pt-BR'
    ? { title: 'Retenção e arquivo', periods: 'Períodos de retenção', archive: 'Arquivo' }
    : { title: 'Retention and archive', periods: 'Retention periods', archive: 'Archive' };
  const [policy, setPolicy] = useState<RetentionPolicy>(defaults);
  const [etag, setEtag] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    read<RetentionPolicy>('/api/settings/retention')
      .then(result => {
        if (controller.signal.aborted) return;
        setPolicy(readPolicy(result.data));
        setEtag(result.etag ?? undefined);
      })
      .catch(reason => {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Could not load retention settings.');
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, []);

  function updateDays(key: keyof RetentionPolicy, raw: string) {
    setSaved(false);
    setPolicy(current => ({ ...current, [key]: raw === '' ? Number.NaN : Number(raw) }));
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaved(false);
    setError(undefined);
    const retentionDays = [policy.logDays, policy.executionDays, policy.auditDays];
    if (retentionDays.some(value => !Number.isInteger(value) || value < 1 || value > 3650) ||
      !Number.isInteger(policy.archiveDays) || policy.archiveDays < 0 || policy.archiveDays > 3650 ||
      (policy.archiveBeforeDelete && policy.archiveDays < 1)) {
      setError('Enter a whole number from 1 to 3650 days for each retention period.');
      return;
    }
    if (!etag) {
      setError('The settings version is missing. Reload this page before saving.');
      return;
    }
    setSaving(true);
    try {
      const savedPolicy = await mutate<RetentionPolicy>('/api/settings/retention', 'PUT', policy, etag);
      setPolicy(readPolicy(savedPolicy));
      const latest = await read<RetentionPolicy>('/api/settings/retention');
      if (latest.etag) setEtag(latest.etag);
      setSaved(true);
    } catch (reason) {
      setError(reason instanceof ApiError && reason.status === 412
        ? 'These settings changed elsewhere. Reload the page to review the latest values.'
        : reason instanceof Error ? reason.message : 'Could not save retention settings.');
    } finally {
      setSaving(false);
    }
  }

  if (loading) return <Box role="status" aria-label="Loading retention settings" sx={{ display: 'flex', justifyContent: 'center', p: 4 }}><CircularProgress /></Box>;

  return (
    <Box component="section" aria-label={sectionCopy.title} sx={{ maxWidth: 760 }}>
      <Stack spacing={3}>
        <Box>
          <Typography variant="h6" component="h2">{sectionCopy.title}</Typography>
          <Typography color="text.secondary" sx={{ mt: 1 }}>Choose how long WorkerDock keeps logs, execution history and audit records.</Typography>
        </Box>
        {error && <Alert severity="error" role="alert">{error}</Alert>}
        {saved && <Alert severity="success" role="status">Retention settings saved.</Alert>}
        <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
          <Box component="form" onSubmit={save}>
            <Stack spacing={3}>
              <Box component="section" aria-label={sectionCopy.periods} sx={{ display: 'grid', gap: 2 }}>
                <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{sectionCopy.periods}</Typography>
              {fields.map(field => (
                <TextField
                  key={field.key}
                  type="number"
                  required
                  fullWidth
                  label={`${field.label} (days)`}
                  helperText={field.help}
                  value={Number.isNaN(policy[field.key]) ? '' : policy[field.key]}
                  onChange={event => updateDays(field.key, event.target.value)}
                  slotProps={{ htmlInput: { min: 1, max: 3650, step: 1, 'aria-label': `${field.label} retention in days` } }}
                />
              ))}
              </Box>
              <Box component="section" aria-label={sectionCopy.archive} sx={{ display: 'grid', gap: 2 }}>
                <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{sectionCopy.archive}</Typography>
                <FormControlLabel
                  control={<Checkbox checked={policy.archiveBeforeDelete} onChange={event => {
                    setSaved(false);
                    setPolicy(current => ({ ...current, archiveBeforeDelete: event.target.checked, archiveDays: event.target.checked ? (current.archiveDays > 0 ? current.archiveDays : 365) : current.archiveDays }));
                  }} />}
                  label="Archive eligible records before deleting them"
                />
                <Typography variant="body2" color="text.secondary" sx={{ ml: 4.5 }}>
                  This controls new deletions. Archive retention below also applies to archive files already created. The location is administrator-configured and is not available here.
                </Typography>
              <TextField
                type="number"
                required={policy.archiveBeforeDelete}
                fullWidth
                label="Archive retention (days)"
                helperText="How long finalized archive files are kept, including files created before archiving was disabled. Set 0 to retain indefinitely when new archival is off."
                value={Number.isNaN(policy.archiveDays) ? '' : policy.archiveDays}
                onChange={event => updateDays('archiveDays', event.target.value)}
                slotProps={{ htmlInput: { min: policy.archiveBeforeDelete ? 1 : 0, max: 3650, step: 1, 'aria-label': 'Archive retention in days' } }}
              />
              </Box>
              <Box sx={{ display: 'flex', justifyContent: 'flex-start' }}>
                <Button type="submit" variant="contained" disabled={saving} sx={{ width: { xs: '100%', sm: 'auto' } }}>{saving ? 'Saving…' : 'Save changes'}</Button>
              </Box>
            </Stack>
          </Box>
        </Paper>
        <Alert severity="info">
          Active executions, unresolved publication intents, open incidents and records needed for safe replay are retained until they are eligible for cleanup. Failed archive or storage operations keep the source records and are reported to operators.
        </Alert>
      </Stack>
    </Box>
  );
}

