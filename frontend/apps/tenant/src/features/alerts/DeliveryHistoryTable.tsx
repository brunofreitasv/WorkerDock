import { useMemo, useState } from "react";
import { Alert, Button, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "../monitoring/CursorPager";
import { PeriodControl } from "../monitoring/PeriodControl";
import { toUtcRange, type HistoryPeriod } from "../monitoring/historyTime";
import { useVisiblePolling } from "../monitoring/useVisiblePolling";
import type { MonitoringPage } from "../monitoring/monitoringTypes";

type Delivery = { deliveryId: string; incidentId: string; ruleName: string; subjectKey: string; kind: string; status: string; attemptCount: number; nextAvailableAtUtc: string; completedAtUtc: string | null; safeCode: string | null };
function displayDate(value: string | null, locale: string, timeZone: string) {
  return value ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date(value)) : "—";
}

export function DeliveryHistoryTable({ language, timeZone }: { language: string; timeZone: string }) {
  const pt = language === "pt-BR"; const locale = pt ? "pt-BR" : "en";
  const label = (en: string, br: string) => pt ? br : en;
  const [period, setPeriod] = useState<HistoryPeriod>({ kind: "30d"});
  const [anchor, setAnchor] = useState(() => new Date());
  const [status, setStatus] = useState(""); const [kind, setKind] = useState("");
  const [cursor, setCursor] = useState(""); const [previous, setPrevious] = useState<string[]>([]); const [refreshSequence, setRefreshSequence] = useState(0);
  const range = useMemo(() => toUtcRange(period, timeZone, anchor, 90), [period, timeZone, anchor]);
  const params = new URLSearchParams({ limit: "50", fromUtc: range.fromUtc, toUtc: range.toUtc });
  if (status) params.set("status", status); if (kind) params.set("kind", kind); if (cursor) params.set("cursor", cursor);
  const url = `/api/alerts/deliveries?${params.toString()}`;
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(
    () => read<MonitoringPage<Delivery>>(url).then(x => x.data), `${url}:${refreshSequence}`,
  );
  const reset = () => { setCursor(""); setPrevious([]); };
  const changePeriod = (value: HistoryPeriod) => { setPeriod(value); setAnchor(new Date()); reset(); };
  const restart = () => { setAnchor(new Date()); reset(); setRefreshSequence(x => x + 1); };
  const statuses = ["Pending", "Leased", "Delivered", "Failed", "Cancelled"] as const;
  const kinds = ["Open", "Reminder", "Resolve"] as const;
  const statusLabel = (value: string) => value === "Pending" ? label("Pending", "Pendente") : value === "Leased" ? label("In progress", "Em andamento") : value === "Delivered" ? label("Delivered", "Entregue") : value === "Failed" ? label("Failed", "Falhou") : label("Cancelled", "Cancelada");
  const kindLabel = (value: string) => value === "Open" ? label("Open", "Abertura") : value === "Reminder" ? label("Reminder", "Lembrete") : label("Resolve", "Resolução");

  return <Paper variant="outlined" sx={{ p: 3 }}>
    <Stack spacing={2}>
      <Typography component="h2" variant="h6">{label("Deliveries", "Entregas")}</Typography>
      <PeriodControl value={period} timeZone={timeZone} maxDays={90} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
      <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
        <TextField select label={label("Delivery status", "Estado da entrega")} value={status} onChange={event => { setStatus(event.target.value); reset(); }}>
          <MenuItem value="">{label("All statuses", "Todos os estados")}</MenuItem>{statuses.map(value => <MenuItem key={value} value={value}>{statusLabel(value)}</MenuItem>)}
        </TextField>
        <TextField select label={label("Delivery kind", "Tipo de entrega")} value={kind} onChange={event => { setKind(event.target.value); reset(); }}>
          <MenuItem value="">{label("All kinds", "Todos os tipos")}</MenuItem>{kinds.map(value => <MenuItem key={value} value={value}>{kindLabel(value)}</MenuItem>)}
        </TextField>
        <Button onClick={restart}>{label("Refresh", "Atualizar")}</Button>
        <Typography variant="caption" sx={{ alignSelf: "center" }}>{label("Updated", "Atualizado")}: {updatedAt?.toLocaleTimeString(locale, { timeZone })}</Typography>
      </Stack>
      {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={restart}>{label("Retry", "Tentar novamente")}</Button>}>
        {label("Delivery history could not be refreshed.", "Não foi possível atualizar o histórico de entregas.")} {stale && label("Showing stale results.", "Exibindo resultados desatualizados.")}
      </Alert>}
      {loading && !data ? <Typography>{label("Loading deliveries…", "Carregando entregas…")}</Typography> : !data ? <Alert severity="error">{label("Delivery history is unavailable.", "Histórico de entregas indisponível.")}</Alert> : <TableContainer sx={{ width: "100%", overflowX: "auto" }}>
        <Table size="small" aria-label={label("Deliveries", "Entregas")}>
          <TableHead><TableRow>{[label("Incident context", "Contexto do incidente"), label("Kind", "Tipo"), label("Status", "Estado"), label("Attempts", "Tentativas"), label("Next attempt", "Próxima tentativa"), label("Completed", "Concluída"), label("Safe code", "Código seguro")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
          <TableBody>{data.items.map(item => <TableRow key={item.deliveryId}>
            <TableCell><Typography>{item.ruleName} · {item.subjectKey}</Typography><Typography variant="caption">{label("Incident", "Incidente")}: {item.incidentId}</Typography></TableCell>
            <TableCell>{kindLabel(item.kind)}</TableCell><TableCell>{statusLabel(item.status)}</TableCell><TableCell>{item.attemptCount}</TableCell>
            <TableCell>{displayDate(item.nextAvailableAtUtc, locale, timeZone)}</TableCell><TableCell>{displayDate(item.completedAtUtc, locale, timeZone)}</TableCell><TableCell>{item.safeCode ?? "—"}</TableCell>
          </TableRow>)}{data.items.length === 0 && <TableRow><TableCell colSpan={7}>{label("No deliveries found.", "Nenhuma entrega encontrada.")}</TableCell></TableRow>}</TableBody>
        </Table>
      </TableContainer>}
      <CursorPager previousCount={previous.length} nextCursor={data?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
        onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
        onNext={() => { if (!data?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(data.nextCursor); }} />
    </Stack>
  </Paper>;
}
