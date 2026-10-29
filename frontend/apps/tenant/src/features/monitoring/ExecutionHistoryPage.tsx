import { useMemo, useState } from "react";
import { Alert, Button, MenuItem, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "./CursorPager";
import { PeriodControl } from "./PeriodControl";
import { toUtcRange, type HistoryPeriod } from "./historyTime";
import { useVisiblePolling } from "./useVisiblePolling";
import type { ExecutionHistoryItemDto, MonitoringPage } from "./monitoringTypes";

const states = ["Queued", "Running", "RetryWaiting", "NeedsReview", "Succeeded", "Failed", "TimedOut", "Stopped", "Skipped"] as const;

function formatDuration(value: string | null) {
  if (!value) return "—";
  return value.split(".")[0];
}

export function ExecutionHistoryPage({ language, timeZone, onExecution }: { language: string; timeZone: string; onExecution(id: string): void }) {
  const pt = language === "pt-BR";
  const locale = pt ? "pt-BR" : "en";
  const label = (en: string, br: string) => pt ? br : en;
  const [period, setPeriod] = useState<HistoryPeriod>({ kind: "7d" });
  const [rangeAnchor, setRangeAnchor] = useState(() => new Date());
  const [search, setSearch] = useState("");
  const [state, setState] = useState("");
  const [templateId, setTemplateId] = useState("");
  const [cursor, setCursor] = useState("");
  const [previous, setPrevious] = useState<string[]>([]);
  const range = useMemo(() => toUtcRange(period, timeZone, rangeAnchor, 90), [period, rangeAnchor, timeZone]);
  const params = new URLSearchParams({ limit: "50", fromUtc: range.fromUtc, toUtc: range.toUtc });
  if (cursor) params.set("cursor", cursor);
  if (search.trim()) params.set("search", search.trim());
  if (state) params.set("state", state);
  if (templateId.trim()) params.set("templateId", templateId.trim());
  const url = `/api/monitoring/executions?${params.toString()}`;
  const { data, loading, error, stale, updatedAt, refresh } = useVisiblePolling(
    () => read<MonitoringPage<ExecutionHistoryItemDto>>(url).then(result => result.data), url,
  );
  const changePeriod = (value: HistoryPeriod) => {
    setPeriod(value);
    setRangeAnchor(new Date());
    setCursor("");
    setPrevious([]);
  };
  const filterChanged = () => { setCursor(""); setPrevious([]); };
  const duration = (value: string | null) => formatDuration(value);
  const statusText = (value: string) => value === "NeedsReview" ? label("Needs review", "Aguardando revisão") : value === "RetryWaiting" ? label("Retry waiting", "Aguardando nova tentativa") : value;

  return <Stack spacing={2}>
    <Stack spacing={1}>
      <PeriodControl value={period} timeZone={timeZone} maxDays={90} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
      <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
        <TextField label={label("Search executions", "Buscar execuções")} value={search} onChange={event => { setSearch(event.target.value); filterChanged(); }} slotProps={{ htmlInput: { maxLength: 100 } }} />
        <TextField label={label("Template", "Modelo")} value={templateId} onChange={event => { setTemplateId(event.target.value); filterChanged(); }} slotProps={{ htmlInput: { maxLength: 100 } }} />
        <TextField select label={label("Status", "Estado")} value={state} onChange={event => { setState(event.target.value); filterChanged(); }}>
          <MenuItem value="">{label("All statuses", "Todos os estados")}</MenuItem>
          {states.map(value => <MenuItem key={value} value={value}>{statusText(value)}</MenuItem>)}
        </TextField>
        <Button onClick={refresh}>{label("Refresh", "Atualizar")}</Button>
        <Typography variant="caption" sx={{ alignSelf: "center" }}>{label("Updated", "Atualizado")}: {updatedAt?.toLocaleTimeString(locale, { timeZone })}</Typography>
      </Stack>
    </Stack>
    {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={refresh}>{label("Retry", "Tentar novamente")}</Button>}>
      {label("Execution history could not be refreshed.", "Não foi possível atualizar o histórico de execuções.")} {stale && label("Showing stale results.", "Exibindo resultados desatualizados.")}
    </Alert>}
    {loading && !data ? <Typography>{label("Loading execution history…", "Carregando histórico de execuções…")}</Typography> : !data ? <Alert severity="error">{label("Execution history is unavailable.", "Histórico de execuções indisponível.")}</Alert> : <TableContainer sx={{ width: "100%", overflowX: "auto" }}>
      <Table size="small" aria-label={label("Execution history", "Histórico de execuções")}>
        <TableHead><TableRow>{[label("Job", "Tarefa"), label("Template", "Modelo"), label("Status", "Estado"), label("Trigger", "Origem"), label("Created", "Criada"), label("Started", "Iniciada"), label("Duration", "Duração"), label("Details", "Detalhes")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
        <TableBody>
          {data.items.map(item => <TableRow key={item.executionId}>
            <TableCell>{item.jobName}</TableCell><TableCell>{item.templateId}</TableCell><TableCell>{statusText(item.state)}</TableCell><TableCell>{item.triggerKind}</TableCell>
            <TableCell>{new Date(item.createdAtUtc).toLocaleString(locale, { timeZone })}</TableCell>
            <TableCell>{item.startedAtUtc ? new Date(item.startedAtUtc).toLocaleString(locale, { timeZone }) : "—"}</TableCell>
            <TableCell>{duration(item.duration)}</TableCell>
            <TableCell><Button size="small" onClick={() => onExecution(item.executionId)} aria-label={`${label("Open", "Abrir")} ${item.jobName}`}>{label("Open", "Abrir")}</Button></TableCell>
          </TableRow>)}
          {data.items.length === 0 && <TableRow><TableCell colSpan={8}>{label("No executions match this filter.", "Nenhuma execução corresponde a este filtro.")}</TableCell></TableRow>}
        </TableBody>
      </Table>
    </TableContainer>}
    <CursorPager previousCount={previous.length} nextCursor={data?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
      onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
      onNext={() => { if (!data?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(data.nextCursor); }} />
  </Stack>;
}
