import { Alert, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from "@mui/material";
import { SectionSurface, StatusBadge } from "@workerdock/ui";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { RuntimeStatusDto } from "./monitoringTypes";

export function WorkersPage({ language }: { language: string }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(() => read<RuntimeStatusDto>("/api/monitoring/workers").then(x => x.data));
  return <Stack spacing={2}>{error && <Alert severity="warning">{pt ? "Não foi possível atualizar o runtime." : "Runtime status could not be refreshed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}
    <SectionSurface title={pt ? "Saúde do runtime" : "Runtime health"}><Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between" }}>{loading && !data ? <Typography>{pt ? "Carregando…" : "Loading…"}</Typography> : <StatusBadge label={data?.ready ? (pt ? "Pronto" : "Ready") : (pt ? "Indisponível" : "Unavailable")} tone={data?.ready ? "success" : "warning"} />}{updatedAt && <Typography variant="caption">{pt ? "Atualizado" : "Updated"}: {updatedAt.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography>}</Stack></SectionSurface>
    <SectionSurface title={pt ? "Detalhes do runtime" : "Runtime details"}>{loading && !data ? <Typography>{pt ? "Carregando…" : "Loading…"}</Typography> : <TableContainer sx={{ overflowX: "auto" }}><Table size="small" aria-label="Runtime" sx={{ minWidth: 560 }}><TableHead><TableRow>{[pt ? "Estado" : "State", "Boot ID", pt ? "Capacidade do host" : "Host capacity", "Heartbeat"].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody><TableRow><TableCell>{data?.ready ? (pt ? "Pronto" : "Ready") : (pt ? "Indisponível" : "Unavailable")}</TableCell><TableCell>{data?.bootId ?? "—"}</TableCell><TableCell>{data?.hostCeiling ?? 1}</TableCell><TableCell>{data?.lastHeartbeatUtc ? new Date(data.lastHeartbeatUtc).toLocaleString(pt ? "pt-BR" : "en") : "—"}</TableCell></TableRow></TableBody></Table></TableContainer>}</SectionSurface>
  </Stack>;
}
