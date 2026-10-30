import { useMemo, useState } from "react";
import { Alert, Button, MenuItem, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "./CursorPager";
import { PeriodControl } from "./PeriodControl";
import { formatDuration, toUtcRange, type HistoryPeriod } from "./historyTime";
import { useVisiblePolling } from "./useVisiblePolling";
import { HistoryFilters, HistoryResults } from "./HistoryLayout";
import { EmptyState, StatusBadge } from "@workerdock/ui";
import type { ExecutionHistoryItemDto, MonitoringPage } from "./monitoringTypes";

const states = ["Queued", "Running", "RetryWaiting", "NeedsReview", "Succeeded", "Failed", "TimedOut", "Stopped", "Skipped"] as const;

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
  const [refreshSequence, setRefreshSequence] = useState(0);
  const range = useMemo(() => toUtcRange(period, timeZone, rangeAnchor, 90), [period, rangeAnchor, timeZone]);
  const params = new URLSearchParams({ limit: "50", fromUtc: range.fromUtc, toUtc: range.toUtc });
  if (cursor) params.set("cursor", cursor);
  if (search.trim()) params.set("search", search.trim());
  if (state) params.set("state", state);
  if (templateId.trim()) params.set("templateId", templateId.trim());
  const url = `/api/monitoring/executions?${params.toString()}`;
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(
    () => read<MonitoringPage<ExecutionHistoryItemDto>>(url).then(result => result.data), `${url}:${refreshSequence}`,
  );
  const changePeriod = (value: HistoryPeriod) => {
    setPeriod(value);
    setRangeAnchor(new Date());
    setCursor("");
    setPrevious([]);
  };
  const restartBrowse = () => {
    setRangeAnchor(new Date());
    setCursor("");
    setPrevious([]);
    setRefreshSequence(sequence => sequence + 1);
  };
  const filterChanged = () => { setCursor(""); setPrevious([]); };
  const duration = (value: string | null) => formatDuration(value);
  const statusText = (value: string) => value === "NeedsReview" ? label("Needs review", "Aguardando revisão") : value === "RetryWaiting" ? label("Retry waiting", "Aguardando nova tentativa") : value;
  const clearFilters = () => { setPeriod({ kind: "7d" }); setRangeAnchor(new Date()); setSearch(""); setState(""); setTemplateId(""); filterChanged(); };
  const statusTone = (value: string): "success" | "error" | "warning" | "info" | "default" => value === "Succeeded" ? "success" : value === "Failed" || value === "TimedOut" ? "error" : value === "NeedsReview" || value === "RetryWaiting" ? "warning" : value === "Running" ? "info" : "default";

  return <Stack spacing={2}>
    <HistoryFilters title={label("Filters", "Filtros")} language={pt ? "pt-BR" : "en"} onClear={clearFilters}>
      <PeriodControl value={period} timeZone={timeZone} maxDays={90} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
      <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 2, flexWrap: "wrap" }}>
        <TextField label={label("Search executions", "Buscar execuções")} value={search} onChange={event => { setSearch(event.target.value); filterChanged(); }} slotProps={{ htmlInput: { maxLength: 100 } }} />
        <TextField label={label("Template", "Modelo")} value={templateId} onChange={event => { setTemplateId(event.target.value); filterChanged(); }} slotProps={{ htmlInput: { maxLength: 100 } }} />
        <TextField select label={label("Status", "Estado")} value={state} onChange={event => { setState(event.target.value); filterChanged(); }} sx={{ minWidth: 180 }}>
          <MenuItem value="">{label("All statuses", "Todos os estados")}</MenuItem>
          {states.map(value => <MenuItem key={value} value={value}>{statusText(value)}</MenuItem>)}
        </TextField>
      </Stack>
    </HistoryFilters>
    <HistoryResults title={label("Results", "Resultados")} onRefresh={restartBrowse} refreshLabel={label("Refresh", "Atualizar")} updatedAt={updatedAt ? `${label("Updated", "Atualizado")}: ${updatedAt.toLocaleTimeString(locale, { timeZone })}` : undefined} footer={<CursorPager previousCount={previous.length} nextCursor={data?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
      onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
      onNext={() => { if (!data?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(data.nextCursor); }} />}>
    <Typography variant="caption" color="text.secondary" sx={{ display: { xs: "block", md: "none" }, mb: 1 }}>{label("Scroll horizontally to view all columns.", "Deslize horizontalmente para ver todas as colunas.")}</Typography>
    {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={restartBrowse}>{label("Retry", "Tentar novamente")}</Button>}>
      {label("Execution history could not be refreshed.", "Não foi possível atualizar o histórico de execuções.")} {stale && label("Showing stale results.", "Exibindo resultados desatualizados.")}
    </Alert>}
    {loading && !data ? <Typography>{label("Loading execution history…", "Carregando histórico de execuções…")}</Typography> : !data ? <Alert severity="error">{label("Execution history is unavailable.", "Histórico de execuções indisponível.")}</Alert> : <><TableContainer sx={{ width: "100%", overflowX: "auto" }}>
      <Table size="small" aria-label={label("Execution history", "Histórico de execuções")} sx={{ minWidth: 950, "& td": { py: 1.5 } }}>
        <TableHead><TableRow>{[label("Job", "Tarefa"), label("Template", "Modelo"), label("Status", "Estado"), label("Trigger", "Origem"), label("Created", "Criada"), label("Started", "Iniciada"), label("Duration", "Duração"), label("Details", "Detalhes")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
        <TableBody>
          {data.items.map(item => <TableRow key={item.executionId}>
            <TableCell>{item.jobName}</TableCell><TableCell>{item.templateId}</TableCell><TableCell><StatusBadge label={statusText(item.state)} tone={statusTone(item.state)} /></TableCell><TableCell>{item.triggerKind}</TableCell>
            <TableCell>{new Date(item.createdAtUtc).toLocaleString(locale, { timeZone })}</TableCell>
            <TableCell>{item.startedAtUtc ? new Date(item.startedAtUtc).toLocaleString(locale, { timeZone }) : "—"}</TableCell>
            <TableCell>{duration(item.duration)}</TableCell>
            <TableCell><Button size="small" onClick={() => onExecution(item.executionId)} aria-label={`${label("Open", "Abrir")} ${item.jobName}`}>{label("Open", "Abrir")}</Button></TableCell>
          </TableRow>)}
        </TableBody>
      </Table>
    </TableContainer>{data.items.length === 0 && <EmptyState title={label("No executions match this filter.", "Nenhuma execução corresponde a este filtro.")} />}</>}
    </HistoryResults>
  </Stack>;
}
