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

export default function TenantDirectory({ tenants, canManage, loading, refreshingId, error, nextCursor, loadingMore, onSearch, onLoadMore, onRefresh, onAdd, onEdit, onRemove }: Props) {
  const [search, setSearch] = useState('');
  const visible = useMemo(() => tenants.filter(tenant => tenant.displayName.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())), [tenants, search]);

  return (
    <Stack spacing={3}>
      {error && <Alert severity="error" role="alert">{error}</Alert>}
      <FilterPanel title="Find tenants">
        <TextField size="small" fullWidth placeholder="Search tenants" value={search} onChange={event => { setSearch(event.target.value); onSearch(event.target.value); }}
          sx={{ maxWidth: { sm: 420 } }}
          slotProps={{ htmlInput: { 'aria-label': 'Search tenants' }, input: { startAdornment: <InputAdornment position="start"><Box component="span" aria-hidden="true">⌕</Box></InputAdornment> } }} />
      </FilterPanel>
      <ResultsPanel title="Tenant directory" meta="Registered links and advisory health observations"
        actions={canManage ? <Button variant="contained" onClick={onAdd}>Add tenant</Button> : undefined}
        footer={nextCursor ? <Button onClick={onLoadMore} disabled={loadingMore}>{loadingMore ? "Loading…" : "Load more tenants"}</Button> : undefined}>
        <Typography variant="caption" color="text.secondary" sx={{ display: { xs: 'block', md: 'none' }, mb: 1 }}>Scroll horizontally to view all columns.</Typography>
        <TableContainer sx={{ overflowX: 'auto' }}>
          <Table aria-label="Registered tenants" sx={{ minWidth: 800 }}>
            <TableHead><TableRow sx={{ bgcolor: '#f8fafc' }}>
              <TableCell>Tenant</TableCell><TableCell>Health</TableCell><TableCell>Last attempt</TableCell><TableCell>Last success</TableCell><TableCell align="right">Actions</TableCell>
            </TableRow></TableHead>
            <TableBody>
              {loading && <TableRow><TableCell colSpan={5}><Typography role="status" color="text.secondary" sx={{ py: 2 }}>Loading registered tenants…</Typography></TableCell></TableRow>}
              {!loading && visible.map(tenant => {
                const state = healthPresentation[tenant.health] ?? healthPresentation.Unknown;
                return <TableRow key={tenant.id} hover>
                  <TableCell>
                    <Stack spacing={0.25}>
                      <Link href={tenant.dashboardUrl} target="_blank" rel="noopener noreferrer" underline="hover" sx={{ width: 'fit-content', fontWeight: 650, color: 'text.primary' }}>
                        {tenant.displayName}<Box component="span" aria-hidden="true" sx={{ ml: 0.5, fontSize: 14 }}>↗</Box>
                      </Link>
                      <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace' }}>{tenant.expectedInstanceId}</Typography>
                    </Stack>
                  </TableCell>
                  <TableCell><Tooltip title="Health is advisory and may be stale while the Control Center is offline."><span><StatusBadge label={state.label} tone={state.color} /></span></Tooltip></TableCell>
                  <TableCell>{formatDistanceToNow(tenant.lastAttemptUtc)}</TableCell>
                  <TableCell>{formatDistanceToNow(tenant.lastSuccessUtc)}</TableCell>
                  <TableCell align="right">
                    <Stack direction="row" spacing={0.5} sx={{ justifyContent: "flex-end" }}>
                      <Tooltip title="Refresh advisory health"><span><Button aria-label={`Refresh health for ${tenant.displayName}`} onClick={() => onRefresh(tenant)} disabled={!!refreshingId} size="small">Refresh</Button></span></Tooltip>
                      {canManage && <>
                        <Tooltip title="Edit registration"><Button aria-label={`Edit ${tenant.displayName}`} onClick={() => onEdit(tenant)} size="small">Edit</Button></Tooltip>
                        <Button color="error" size="small" onClick={() => onRemove(tenant)}>Remove</Button>
                      </>}
                    </Stack>
                  </TableCell>
                </TableRow>;
              })}
            </TableBody>
          </Table>
        </TableContainer>
        {!loading && visible.length === 0 && <EmptyState title={search ? 'No matching tenants' : 'No tenants registered yet'} description={search ? 'Try another search.' : canManage ? 'Add a tenant link to see its advisory health here.' : 'A manager can add tenant links to this directory.'} />}
      </ResultsPanel>
      <Alert severity="info">Health checks are advisory. A tenant can keep running if this Control Center is offline; a stale or unknown result does not prove that the tenant is stopped.</Alert>
    </Stack>
  );
}
