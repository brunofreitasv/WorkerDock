import { useMemo, useState } from 'react';
import { Alert, Box, Button, InputAdornment, Link, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import { EmptyState, FilterPanel, ResultsPanel, StatusBadge } from '@workerdock/ui';
import { formatDistanceToNow } from './time';

export type TenantHealth = 'Healthy' | 'Unhealthy' | 'Stale' | 'Unknown' | 'IdentityMismatch';
export type TenantEntry = {
  id: string;
  displayName: string;
  dashboardUrl: string;
  health: TenantHealth;
  lastAttemptUtc?: string | null;
  lastSuccessUtc?: string | null;
  expectedInstanceId: string;
  version?: string;
};

type Props = {
  locale?: 'en' | 'pt-BR';
  tenants: TenantEntry[];
  canManage: boolean;
  loading: boolean;
  refreshingId?: string;
  nextCursor?: string | null;
  loadingMore?: boolean;
  onSearch: (query: string) => void;
  onLoadMore: () => void;
  error?: string;
  onRefresh: (tenant: TenantEntry) => void;
  onAdd: () => void;
  onEdit: (tenant: TenantEntry) => void;
  onRemove: (tenant: TenantEntry) => void;
};

const healthPresentation: Record<TenantHealth, { label: string; color: 'success' | 'error' | 'warning' | 'default' }> = {
  Healthy: { label: 'Healthy', color: 'success' },
  Unhealthy: { label: 'Unhealthy', color: 'error' },
  Stale: { label: 'Stale', color: 'warning' },
  Unknown: { label: 'Unknown', color: 'default' },
  IdentityMismatch: { label: 'Identity mismatch', color: 'error' },
};

const directoryCopy = {
  en: { find: 'Find tenants', search: 'Search tenants', title: 'Tenant directory', meta: 'Registered links and advisory health observations', add: 'Add tenant', loadMore: 'Load more tenants', loadingMore: 'Loading…', scroll: 'Scroll horizontally to view all columns.', table: 'Registered tenants', tenant: 'Tenant', health: 'Health', lastAttempt: 'Last attempt', lastSuccess: 'Last success', actions: 'Actions', loading: 'Loading registered tenants…', advisoryTooltip: 'Health is advisory and may be stale while the Control Center is offline.', refreshTooltip: 'Refresh advisory health', refresh: 'Refresh', refreshFor: 'Refresh health for', editTooltip: 'Edit registration', edit: 'Edit', remove: 'Remove', noMatch: 'No matching tenants', noTenants: 'No tenants registered yet', trySearch: 'Try another search.', addHint: 'Add a tenant link to see its advisory health here.', managerHint: 'A manager can add tenant links to this directory.', advisory: 'Health checks are advisory. A tenant can keep running if this Control Center is offline; a stale or unknown result does not prove that the tenant is stopped.', healthy: 'Healthy', unhealthy: 'Unhealthy', stale: 'Stale', unknown: 'Unknown', mismatch: 'Identity mismatch' },
  'pt-BR': { find: 'Buscar tenants', search: 'Buscar tenants', title: 'Diretório de tenants', meta: 'Links cadastrados e observações de saúde informativas', add: 'Adicionar tenant', loadMore: 'Carregar mais tenants', loadingMore: 'Carregando…', scroll: 'Deslize horizontalmente para ver todas as colunas.', table: 'Tenants cadastrados', tenant: 'Tenant', health: 'Saúde', lastAttempt: 'Última tentativa', lastSuccess: 'Último sucesso', actions: 'Ações', loading: 'Carregando tenants cadastrados…', advisoryTooltip: 'A saúde é informativa e pode estar desatualizada enquanto a Central de controle estiver offline.', refreshTooltip: 'Atualizar saúde informativa', refresh: 'Atualizar', refreshFor: 'Atualizar saúde de', editTooltip: 'Editar cadastro', edit: 'Editar', remove: 'Remover', noMatch: 'Nenhum tenant encontrado', noTenants: 'Nenhum tenant cadastrado ainda', trySearch: 'Tente outra busca.', addHint: 'Adicione um link de tenant para ver sua saúde informativa aqui.', managerHint: 'Um gestor pode adicionar links de tenant a este diretório.', advisory: 'As verificações de saúde são informativas. Um tenant pode continuar em execução se esta Central de controle estiver offline; um resultado desatualizado ou desconhecido não significa que o tenant parou.', healthy: 'Saudável', unhealthy: 'Não saudável', stale: 'Desatualizado', unknown: 'Desconhecido', mismatch: 'Identidade divergente' },
};

export default function TenantDirectory({ locale = 'en', tenants, canManage, loading, refreshingId, error, nextCursor, loadingMore, onSearch, onLoadMore, onRefresh, onAdd, onEdit, onRemove }: Props) {
  const [search, setSearch] = useState('');
  const t = directoryCopy[locale];
  const visible = useMemo(() => tenants.filter(tenant => tenant.displayName.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())), [tenants, search]);

  return (
    <Stack spacing={3}>
      {error && <Alert severity="error" role="alert">{error}</Alert>}
      <FilterPanel title={t.find}>
        <TextField size="small" fullWidth placeholder={t.search} value={search} onChange={event => { setSearch(event.target.value); onSearch(event.target.value); }}
          sx={{ maxWidth: { sm: 420 } }}
          slotProps={{ htmlInput: { 'aria-label': t.search }, input: { startAdornment: <InputAdornment position="start"><Box component="span" aria-hidden="true">⌕</Box></InputAdornment> } }} />
      </FilterPanel>
      <ResultsPanel title={t.title} meta={t.meta}
        actions={canManage ? <Button variant="contained" onClick={onAdd}>{t.add}</Button> : undefined}
        footer={nextCursor ? <Button onClick={onLoadMore} disabled={loadingMore}>{loadingMore ? t.loadingMore : t.loadMore}</Button> : undefined}>
        <Typography variant="caption" color="text.secondary" sx={{ display: { xs: 'block', md: 'none' }, mb: 1 }}>{t.scroll}</Typography>
        <TableContainer sx={{ overflowX: 'auto' }}>
          <Table aria-label={t.table} sx={{ minWidth: 800 }}>
            <TableHead><TableRow sx={{ bgcolor: '#f8fafc' }}>
              <TableCell>{t.tenant}</TableCell><TableCell>{t.health}</TableCell><TableCell>{t.lastAttempt}</TableCell><TableCell>{t.lastSuccess}</TableCell><TableCell align="right">{t.actions}</TableCell>
            </TableRow></TableHead>
            <TableBody>
              {loading && <TableRow><TableCell colSpan={5}><Typography role="status" color="text.secondary" sx={{ py: 2 }}>{t.loading}</Typography></TableCell></TableRow>}
              {!loading && visible.map(tenant => {
                const state = healthPresentation[tenant.health] ?? healthPresentation.Unknown;
                const stateLabel = ({ Healthy: t.healthy, Unhealthy: t.unhealthy, Stale: t.stale, Unknown: t.unknown, IdentityMismatch: t.mismatch })[tenant.health] ?? t.unknown;
                return <TableRow key={tenant.id} hover>
                  <TableCell>
                    <Stack spacing={0.25}>
                      <Link href={tenant.dashboardUrl} target="_blank" rel="noopener noreferrer" underline="hover" sx={{ width: 'fit-content', fontWeight: 650, color: 'text.primary' }}>
                        {tenant.displayName}<Box component="span" aria-hidden="true" sx={{ ml: 0.5, fontSize: 14 }}>↗</Box>
                      </Link>
                      <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace' }}>{tenant.expectedInstanceId}</Typography>
                    </Stack>
                  </TableCell>
                  <TableCell><Tooltip title={t.advisoryTooltip}><span><StatusBadge label={stateLabel} tone={state.color} /></span></Tooltip></TableCell>
                  <TableCell>{formatDistanceToNow(tenant.lastAttemptUtc, locale)}</TableCell>
                  <TableCell>{formatDistanceToNow(tenant.lastSuccessUtc, locale)}</TableCell>
                  <TableCell align="right">
                    <Stack direction="row" spacing={0.5} sx={{ justifyContent: "flex-end" }}>
                      <Tooltip title={t.refreshTooltip}><span><Button aria-label={`${t.refreshFor} ${tenant.displayName}`} onClick={() => onRefresh(tenant)} disabled={!!refreshingId} size="small">{t.refresh}</Button></span></Tooltip>
                      {canManage && <>
                        <Tooltip title={t.editTooltip}><Button aria-label={`${t.edit} ${tenant.displayName}`} onClick={() => onEdit(tenant)} size="small">{t.edit}</Button></Tooltip>
                        <Button color="error" size="small" onClick={() => onRemove(tenant)}>{t.remove}</Button>
                      </>}
                    </Stack>
                  </TableCell>
                </TableRow>;
              })}
            </TableBody>
          </Table>
        </TableContainer>
        {!loading && visible.length === 0 && <EmptyState title={search ? t.noMatch : t.noTenants} description={search ? t.trySearch : canManage ? t.addHint : t.managerHint} />}
      </ResultsPanel>
      <Alert severity="info">{t.advisory}</Alert>
    </Stack>
  );
}
