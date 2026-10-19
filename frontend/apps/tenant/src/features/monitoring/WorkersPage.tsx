import { Alert, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { WorkerSummaryDto } from "./monitoringTypes";

export function WorkersPage({ language }: { language: string }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(() => read<WorkerSummaryDto[]>("/api/monitoring/workers").then(x => x.data));
  return <Stack spacing={2}>{error && <Alert severity="warning">{pt ? "Não foi possível atualizar os workers." : "Worker status could not be refreshed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}<Typography variant="caption">{pt ? "Atualizado" : "Updated"}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography>{loading && !data ? <Typography>{pt ? "Carregando…" : "Loading…"}</Typography> : <Table size="small" aria-label={pt ? "Workers" : "Workers"}><TableHead><TableRow>{[pt ? "Estado" : "State", "ID", pt ? "Capacidade" : "Capacity", pt ? "Heartbeat" : "Heartbeat", pt ? "Capacidades" : "Capabilities"].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody>{data?.map(worker => <TableRow key={worker.installationId}><TableCell>{worker.state}</TableCell><TableCell>{worker.installationId}</TableCell><TableCell>{worker.liveLimit}/{worker.capacityCeiling}</TableCell><TableCell>{new Date(worker.lastHeartbeatUtc).toLocaleString(pt ? "pt-BR" : "en")}</TableCell><TableCell>{worker.capabilities.join(", ") || "—"}</TableCell></TableRow>)}{data?.length === 0 && <TableRow><TableCell colSpan={5}>{pt ? "Nenhum worker registrado." : "No workers registered."}</TableCell></TableRow>}</TableBody></Table>}</Stack>;
}
