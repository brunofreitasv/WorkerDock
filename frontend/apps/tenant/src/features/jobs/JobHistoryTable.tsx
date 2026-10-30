import { useEffect, useMemo, useState } from "react";
import { Alert, Button, MenuItem, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "../monitoring/CursorPager";
import { PeriodControl } from "../monitoring/PeriodControl";
import { formatDuration, toUtcRange, type HistoryPeriod } from "../monitoring/historyTime";
import { HistoryFilters, HistoryResults } from "../monitoring/HistoryLayout";
import { EmptyState, StatusBadge } from "@workerdock/ui";

type Item = { executionId: string; jobName: string; templateId: string; triggerKind: string; state: string; createdAtUtc: string; startedAtUtc: string | null; duration: string | null };
type Page = { items: Item[]; nextCursor: string | null };
const states = ["Queued", "Running", "RetryWaiting", "NeedsReview", "Succeeded", "Failed", "TimedOut", "Stopped", "Skipped"] as const;

export function JobHistoryTable({ jobId, language, timeZone }: { jobId: string; language: string; timeZone: string }) {
  const pt = language === "pt-BR";
  const locale = pt ? "pt-BR" : "en";
  const label = (en: string, br: string) => pt ? br : en;
  const [period, setPeriod] = useState<HistoryPeriod>({ kind: "7d" });
  const [rangeAnchor, setRangeAnchor] = useState(() => new Date());
  const [state, setState] = useState("");
  const [cursor, setCursor] = useState("");
  const [previous, setPrevious] = useState<string[]>([]);
  const [refreshSequence, setRefreshSequence] = useState(0);
  const [page, setPage] = useState<Page>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date>();
  const range = useMemo(() => toUtcRange(period, timeZone, rangeAnchor, 90), [period, rangeAnchor, timeZone]);
  const params = new URLSearchParams({ limit: "50", fromUtc: range.fromUtc, toUtc: range.toUtc });
  if (cursor) params.set("cursor", cursor);
  if (state) params.set("state", state);
  const requestPath = `/api/monitoring/jobs/${encodeURIComponent(jobId)}/executions?${params.toString()}`;

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(false);
    void read<Page>(requestPath).then(({ data }) => {
      if (active) { setPage(data); setUpdatedAt(new Date()); }
    }).catch(() => {
      if (active) setError(true);
    }).finally(() => {
      if (active) setLoading(false);
    });
    return () => { active = false; };
  }, [requestPath, refreshSequence]);

  const reset = () => { setCursor(""); setPrevious([]); };
  const changePeriod = (value: HistoryPeriod) => { setPeriod(value); setRangeAnchor(new Date()); reset(); };
  const refresh = () => { setRangeAnchor(new Date()); reset(); setRefreshSequence(sequence => sequence + 1); };
  const clearFilters = () => { setPeriod({ kind: "7d" }); setRangeAnchor(new Date()); setState(""); reset(); };
  const statusText = (value: string) => {
    const localized: Record<string, [string, string]> = {
      Queued: ["Queued", "Na fila"], Running: ["Running", "Em execução"], RetryWaiting: ["Retry waiting", "Aguardando nova tentativa"],
      NeedsReview: ["Needs review", "Aguardando revisão"], Succeeded: ["Succeeded", "Concluída"], Failed: ["Failed", "Falhou"],
      TimedOut: ["Timed out", "Tempo limite excedido"], Stopped: ["Stopped", "Interrompida"], Skipped: ["Skipped", "Ignorada"],
    };
    const translated = localized[value];
    return translated ? translated[pt ? 1 : 0] : value;
  };
  const duration = (value: string | number | null) => typeof value === "string" ? formatDuration(value) : value == null ? "—" : String(value);
  const statusTone = (value: string): "success" | "error" | "warning" | "info" | "default" => value === "Succeeded" ? "success" : value === "Failed" || value === "TimedOut" ? "error" : value === "NeedsReview" || value === "RetryWaiting" ? "warning" : value === "Running" ? "info" : "default";

  return <Stack spacing={2}>
    <Typography component="h2" variant="h6">{label("Job history", "Histórico da tarefa")} · {jobId}</Typography>
    <HistoryFilters title={label("Filters", "Filtros")} language={pt ? "pt-BR" : "en"} onClear={clearFilters}>
    <PeriodControl value={period} timeZone={timeZone} maxDays={90} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 2 }}>
      <TextField select label={label("Status", "Estado")} value={state} onChange={event => { setState(event.target.value); reset(); }} sx={{ minWidth: 180 }}>
        <MenuItem value="">{label("All statuses", "Todos os estados")}</MenuItem>
        {states.map(value => <MenuItem key={value} value={value}>{statusText(value)}</MenuItem>)}
      </TextField>
    </Stack>
    </HistoryFilters>
    <HistoryResults title={label("Results", "Resultados")} onRefresh={refresh} refreshLabel={label("Refresh", "Atualizar")} updatedAt={updatedAt ? `${label("Updated", "Atualizado")}: ${updatedAt.toLocaleTimeString(locale, { timeZone })}` : undefined} footer={<CursorPager previousCount={previous.length} nextCursor={page?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
      onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
      onNext={() => { if (!page?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(page.nextCursor); }} />}>
    <Typography variant="caption" color="text.secondary" sx={{ display: { xs: "block", md: "none" }, mb: 1 }}>{label("Scroll horizontally to view all columns.", "Deslize horizontalmente para ver todas as colunas.")}</Typography>
    {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={refresh}>{label("Retry", "Tentar novamente")}</Button>}>
      {label("Job history could not be refreshed.", "Não foi possível atualizar o histórico da tarefa.")} {page && label("Showing previous results.", "Exibindo resultados anteriores.")}
    </Alert>}
    {loading && !page ? <Typography role="status">{label("Loading job history…", "Carregando histórico da tarefa…")}</Typography> : !page ? <Alert severity="error">{label("Job history is unavailable.", "Histórico da tarefa indisponível.")}</Alert> : <><TableContainer sx={{ width: "100%", overflowX: "auto" }}>
      <Table size="small" aria-label={label("Job execution history", "Histórico de execuções da tarefa")} sx={{ minWidth: 900, "& td": { py: 1.5 } }}>
        <TableHead><TableRow>{[label("Execution", "Execução"), label("Template", "Modelo"), label("Status", "Estado"), label("Trigger", "Origem"), label("Created", "Criada"), label("Started", "Iniciada"), label("Duration", "Duração")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
        <TableBody>
          {page.items.map(item => <TableRow key={item.executionId}>
            <TableCell><a href={`#/execution:${item.executionId}`}>{item.executionId}</a></TableCell>
            <TableCell>{item.templateId}</TableCell><TableCell><StatusBadge label={statusText(item.state)} tone={statusTone(item.state)} /></TableCell><TableCell>{item.triggerKind}</TableCell>
            <TableCell>{new Date(item.createdAtUtc).toLocaleString(locale, { timeZone })}</TableCell>
            <TableCell>{item.startedAtUtc ? new Date(item.startedAtUtc).toLocaleString(locale, { timeZone }) : "—"}</TableCell><TableCell>{duration(item.duration)}</TableCell>
          </TableRow>)}
        </TableBody>
      </Table>
    </TableContainer>{!page.items.length && <EmptyState title={label("No executions match this filter.", "Nenhuma execução corresponde a este filtro.")} />}</>}
    </HistoryResults>
  </Stack>;
}
