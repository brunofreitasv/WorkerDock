import { useCallback, useEffect, useMemo, useState } from 'react';
import { Alert, Box, Button, Container, FormControl, MenuItem, Select, Stack, Typography } from '@mui/material';
import TenantDirectory, { type TenantEntry } from './TenantDirectory';
import RegistrationEditor, { type TenantRegistrationInput } from './RegistrationEditor';
import { BrandMark, PageHeader } from '@workerdock/ui';

type Locale = 'en' | 'pt-BR';
type SessionDto = {
  authenticated: boolean;
  canRead: boolean;
  canManage: boolean;
  mode: string;
  language: string;
  timeZone: string;
};
type Copy = {
  title: string;
  subtitle: string;
  manager: string;
  skip: string;
  signOut: string;
  refreshDirectory: string;
  footer: string;
  refreshFailed: string;
  saveFailed: string;
  removeConfirm: (name: string) => string;
};

const copy: Record<Locale, Copy> = {
  en: {
    title: 'Control Center',
    subtitle: 'A central directory for your WorkerDock tenant instances.',
    manager: 'Manager access',
    skip: 'Skip to content',
    signOut: 'Sign out',
    refreshDirectory: 'Refresh directory',
    footer: 'WorkerDock Control Center · Tenant access remains protected by each tenant’s own authentication.',
    refreshFailed: 'Could not refresh health. Try again later.',
    saveFailed: 'Could not save the tenant registration.',
    removeConfirm: name => `Remove the link for ${name}? This does not change or stop the tenant.`,
  },
  'pt-BR': {
    title: 'Central de controle',
    subtitle: 'Um diretório central das instâncias de tenant do WorkerDock.',
    manager: 'Acesso de gestor',
    skip: 'Ir para o conteúdo',
    signOut: 'Sair',
    refreshDirectory: 'Atualizar diretório',
    footer: 'Central de controle WorkerDock · O acesso a cada tenant continua protegido pela própria autenticação.',
    refreshFailed: 'Não foi possível atualizar a saúde. Tente novamente mais tarde.',
    saveFailed: 'Não foi possível salvar o cadastro do tenant.',
    removeConfirm: name => `Remover o link de ${name}? Isso não altera nem interrompe o tenant.`,
  },
};

let antiforgeryToken: string | undefined;

async function getAntiforgeryToken(): Promise<string> {
  if (antiforgeryToken) return antiforgeryToken;
  const response = await fetch('/api/antiforgery', { credentials: 'same-origin' });
  if (!response.ok) throw new Error(`Could not establish request protection (${response.status}).`);
  const body = await response.json() as { requestToken?: string; RequestToken?: string };
  const token = body.requestToken ?? body.RequestToken;
  if (!token) throw new Error('The antiforgery response did not include a request token.');
  antiforgeryToken = token;
  return token;
}
async function request(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers);
  if (init.body) headers.set('Content-Type', 'application/json');
  if (init.method && init.method !== 'GET') headers.set('X-WorkerDock-CSRF', await getAntiforgeryToken());
  const response = await fetch(path, { ...init, headers, credentials: 'same-origin' });
  if (!response.ok) {
    if (response.status === 401 || response.status === 403) throw new Error('Your session does not allow this action.');
    if (response.status === 412) throw new Error('This registration changed elsewhere. Reload and try again.');
    throw new Error(`Request failed (${response.status}).`);
  }
  return response;
}

export default function App() {
  const [locale, setLocale] = useState<Locale>('en');
  const [tenants, setTenants] = useState<TenantEntry[]>([]);
  const [search, setSearch] = useState("");
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [canManage, setCanManage] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [dialogOpen, setDialogOpen] = useState(false);
  const [selected, setSelected] = useState<TenantEntry>();
  const [saving, setSaving] = useState(false);
  const [refreshingId, setRefreshingId] = useState<string>();
  const t = copy[locale];

  const load = useCallback(async (query: string, cursor?: string | null, append = false, signal?: AbortSignal) => {
    setError("");
    if (append) setLoadingMore(true);
    else setLoading(true);
    try {
      const params = new URLSearchParams({ search: query, cursor: cursor ?? "", limit: "50" });
      const response = await fetch(`/api/tenants?${params}`, { credentials: "same-origin", signal });
      if (!response.ok) throw new Error(`Could not load tenants (${response.status}).`);
      const page = await response.json() as { items?: TenantEntry[]; nextCursor?: string | null };
      if (!Array.isArray(page.items)) throw new Error("The tenant directory response is invalid.");
      setTenants(current => append ? [...current, ...page.items!] : page.items!);
      setNextCursor(page.nextCursor ?? null);
    } catch (reason) {
      if (signal?.aborted) return;
      setError(reason instanceof Error ? reason.message : "Could not load the tenant directory.");
    } finally {
      if (!signal?.aborted) {
        setLoading(false);
        setLoadingMore(false);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(search, undefined, false, controller.signal);
    return () => controller.abort();
  }, [load, search]);

  useEffect(() => {
    const controller = new AbortController();
    fetch("/api/session", { credentials: "same-origin", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Could not load session access (${response.status}).`);
        return await response.json() as SessionDto;
      })
      .then(session => {
        setCanManage(session.canManage);
        if (session.language.toLocaleLowerCase().startsWith("pt")) setLocale("pt-BR");
      })
      .catch(reason => {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Could not load session access.");
      });
    return () => controller.abort();
  }, []);

  const registrationInput = useMemo(() => selected ? ({
    displayName: selected.displayName,
    expectedInstanceId: selected.expectedInstanceId,
    dashboardUrl: selected.dashboardUrl,
    healthCredentialReference: "",
  }) : undefined, [selected]);

  async function refresh(tenant: TenantEntry) {
    setRefreshingId(tenant.id);
    setError('');
    try {
      await request(`/api/tenants/${encodeURIComponent(tenant.id)}/health/refresh`, { method: 'POST' });
      await load(search);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t.refreshFailed);
    } finally {
      setRefreshingId(undefined);
    }
  }

  async function save(input: TenantRegistrationInput) {
    setSaving(true);
    setError('');
    try {
      const path = selected ? `/api/tenants/${encodeURIComponent(selected.id)}` : '/api/tenants';
      const response = await request(path, {
        method: selected ? 'PUT' : 'POST',
        headers: selected?.version ? { 'If-Match': '"' + selected.version + '"' } : undefined,
        // Registration sends a server-managed vault reference; raw health credentials never enter the browser.
        body: JSON.stringify(input),
      });
      if (!response.ok) throw new Error(t.saveFailed);
      setDialogOpen(false);
      setSelected(undefined);
      await load(search);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t.saveFailed);
    } finally {
      setSaving(false);
    }
  }

  async function signOut() {
    try {
      const response = await request('/auth/signout', { method: 'POST' });
      const result = await response.json() as { redirectUrl: string };
      window.location.assign(result.redirectUrl);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not sign out.');
    }
  }
  async function remove(tenant: TenantEntry) {
    if (!window.confirm(t.removeConfirm(tenant.displayName))) return;
    setError('');
    try {
      await request(`/api/tenants/${encodeURIComponent(tenant.id)}`, {
        method: 'DELETE',
        headers: tenant.version ? { 'If-Match': '"' + tenant.version + '"' } : undefined,
      });
      await load(search);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not remove tenant link.');
    }
  }

  function openEditor(tenant?: TenantEntry) {
    setSelected(tenant);
    setDialogOpen(true);
  }

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <Box component="a" href="#main-content" sx={{ position: 'absolute', left: 8, top: -64, zIndex: 2000, bgcolor: 'background.paper', p: 1, '&:focus': { top: 8 } }}>{t.skip}</Box>
      <Box component="header" sx={{ borderBottom: '1px solid', borderColor: 'divider', bgcolor: 'background.paper' }}>
        <Container maxWidth="xl" sx={{ py: 2.25 }}>
          <Stack direction="row" sx={{ alignItems: "center", justifyContent: "space-between", gap: 2, flexWrap: 'wrap' }}>
            <Stack direction="row" spacing={1.25} sx={{ alignItems: "center" }}>
              <BrandMark size={36} />
              <Typography variant="subtitle1" sx={{ fontWeight: 750 }}>WorkerDock</Typography>
              <Typography variant="body2" color="text.secondary" sx={{ display: { xs: 'none', sm: 'block' } }}>{t.title}</Typography>
            </Stack>
            <Stack direction="row" spacing={1} sx={{ alignItems: "center", flexWrap: 'wrap' }}>
              {canManage && <Typography variant="caption" color="text.secondary" sx={{ display: { xs: 'none', md: 'block' } }}>{t.manager}</Typography>}
              <FormControl size="small" variant="standard">
                <Select value={locale} onChange={event => setLocale(event.target.value as Locale)} inputProps={{ 'aria-label': 'Language' }} disableUnderline>
                  <MenuItem value="en">English</MenuItem>
                  <MenuItem value="pt-BR">Português (Brasil)</MenuItem>
                </Select>
              </FormControl>
              <Button onClick={() => void signOut()} color="inherit" size="small">{t.signOut}</Button>
            </Stack>
          </Stack>
        </Container>
      </Box>
      <Container component="main" id="main-content" tabIndex={-1} maxWidth="xl" sx={{ py: { xs: 3, md: 5 } }}>
        <Stack spacing={3.5}>
          <PageHeader title={t.title} description={t.subtitle} actions={<Button variant="outlined" onClick={() => void load(search)} disabled={loading}>{t.refreshDirectory}</Button>} />
          {error && <Alert severity="error" role="alert">{error}</Alert>}
          <TenantDirectory
            locale={locale}
            tenants={tenants}
            onSearch={setSearch}
            nextCursor={nextCursor}
            loadingMore={loadingMore}
            onLoadMore={() => void load(search, nextCursor, true)}
            canManage={canManage}
            loading={loading}
            refreshingId={refreshingId}
            onRefresh={refresh}
            onAdd={() => openEditor()}
            onEdit={tenant => openEditor(tenant)}
            onRemove={tenant => void remove(tenant)}
          />
          <Typography component="footer" variant="caption" color="text.secondary" sx={{ textAlign: 'center' }}>
            {t.footer}
          </Typography>
        </Stack>
      </Container>
      <RegistrationEditor locale={locale} open={dialogOpen} initialValue={registrationInput} busy={saving} onClose={() => { setDialogOpen(false); setSelected(undefined); }} onSave={save} />
    </Box>
  );
}

