import { useState } from "react";
import { Alert, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { AuditSummaryDto, MonitoringPage } from "./monitoringTypes";

export function AuditPage({ language }: { language: string }) {
  const pt = language === "pt-BR"; const [search, setSearch] = useState(""); const [cursor, setCursor] = useState(""); const [previous, setPrevious] = useState<string[]>([]);
  const url = `/api/audit?limit=50${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}${search ? `&search=${encodeURIComponent(search)}` : ""}`;
  const { data, loading, error, stale, updatedAt, refresh } = useVisiblePolling(() => read<MonitoringPage<AuditSummaryDto>>(url).then(x => x.data), url);
  return <Stack spacing={2}>
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1}><TextField label={pt ? "Buscar auditoria" : "Search audit"} value={search} onChange={e => { setSearch(e.target.value); setCursor(""); setPrevious([]); }} /><Button onClick={refresh}>{pt ? "Atualizar" : "Refresh"}</Button><Typography variant="caption" sx={{ alignSelf: "center" }}>{pt ? "Atualizado" : "Updated"}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography></Stack>
    {error && <Alert severity="warning">{pt ? "Não foi possível atualizar a auditoria." : "Audit data could not be refreshed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}
    {loading && !data ? <Typography>{pt ? "Carregando…" : "Loading…"}</Typography> : <Table size="small" aria-label={pt ? "Auditoria" : "Audit"}><TableHead><TableRow>{[pt ? "Horário UTC" : "Time UTC", pt ? "Ação" : "Action", pt ? "Ator" : "Actor", pt ? "Modo" : "Mode", pt ? "Correlação" : "Correlation"].map(x => <TableCell key={x}>{x}</TableCell>)}</TableRow></TableHead><TableBody>{(data?.items ?? []).map(event => <TableRow key={event.eventId}><TableCell>{new Date(event.occurredAtUtc).toLocaleString(pt ? "pt-BR" : "en", { timeZone: "UTC" })}</TableCell><TableCell>{event.eventType}</TableCell><TableCell>{event.actorSubject}</TableCell><TableCell>{event.authenticationMode}</TableCell><TableCell>{event.correlationId}</TableCell></TableRow>)}{(data?.items ?? []).length === 0 && <TableRow><TableCell colSpan={5}>{pt ? "Nenhum evento encontrado." : "No audit events found."}</TableCell></TableRow>}</TableBody></Table>}
    <Stack direction="row" spacing={1}><Button disabled={!previous.length} onClick={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}>{pt ? "Anterior" : "Previous"}</Button><Button disabled={!data?.nextCursor} onClick={() => { setPrevious(stack => [...stack, cursor]); setCursor(data?.nextCursor ?? ""); }}>{pt ? "Próxima" : "Next"}</Button></Stack>
  </Stack>;
}
