import { Alert, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { RuntimeStatusDto } from "./monitoringTypes";

export function WorkersPage({ language }: { language: string }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(() => read<RuntimeStatusDto>("/api/monitoring/workers").then(x => x.data));
  return <Stack spacing={2}>{error && <Alert severity="warning">{pt ? "Não foi possível atualizar o runtime." : "Runtime status could not be refreshed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}<Typography variant="caption">{pt ? "Atualizado" : "Updated"}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography>{loading && !data ? <Typography>{pt ? "Carregando…" : "Loading…"}</Typography> : <Table size="small" aria-label="Runtime"><TableHead><TableRow>{[pt ? "Estado" : "State", "Boot ID", pt ? "Capacidade do host" : "Host capacity", "Heartbeat"].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody><TableRow><TableCell>{data?.ready ? "Ready" : "Unavailable"}</TableCell><TableCell>{data?.bootId ?? "—"}</TableCell><TableCell>{data?.hostCeiling ?? 1}</TableCell><TableCell>{data?.lastHeartbeatUtc ? new Date(data.lastHeartbeatUtc).toLocaleString(pt ? "pt-BR" : "en") : "—"}</TableCell></TableRow></TableBody></Table>}</Stack>;
}
