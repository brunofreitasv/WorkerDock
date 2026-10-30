import { useEffect, useMemo, useState } from "react";
import { Alert, Button, MenuItem, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "./CursorPager";
import { PeriodControl } from "./PeriodControl";
import { toUtcRange, type HistoryPeriod } from "./historyTime";
import { useVisiblePolling } from "./useVisiblePolling";
import { HistoryFilters, HistoryResults } from "./HistoryLayout";
import { EmptyState, StatusBadge } from "@workerdock/ui";
import type { AuditSummaryDto, MonitoringPage } from "./monitoringTypes";

export function AuditPage({ language, timeZone }: { language: string; timeZone: string }) {
  const pt = language === "pt-BR";
  const locale = pt ? "pt-BR" : "en";
  const label = (en: string, br: string) => pt ? br : en;
  const [period, setPeriod] = useState<HistoryPeriod>({ kind: "7d" });
  const [rangeAnchor, setRangeAnchor] = useState(() => new Date());
  const [search, setSearch] = useState("");
  const [eventType, setEventType] = useState("");
  const [cursor, setCursor] = useState("");
  const [previous, setPrevious] = useState<string[]>([]);
  const [refreshSequence, setRefreshSequence] = useState(0);
  const [eventTypes, setEventTypes] = useState<string[]>([]);
  const [eventTypesUnavailable, setEventTypesUnavailable] = useState(false);
  const range = useMemo(() => toUtcRange(period, timeZone, rangeAnchor, 365), [period, rangeAnchor, timeZone]);
  const params = new URLSearchParams({ limit: "50", fromUtc: range.fromUtc, toUtc: range.toUtc });
  if (cursor) params.set("cursor", cursor);
  if (search.trim()) params.set("search", search.trim());
  if (eventType) params.set("eventType", eventType);
  const url = `/api/audit?${params.toString()}`;
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(
    () => read<MonitoringPage<AuditSummaryDto>>(url).then(result => result.data), `${url}:${refreshSequence}`,
  );
  const auditItems = Array.isArray(data?.items) ? data.items : [];

  useEffect(() => {
    let active = true;
    void read<string[]>("/api/audit/event-types").then(result => {
      if (active) setEventTypes(Array.isArray(result.data) ? result.data.filter((value): value is string => typeof value === "string") : []);
    }).catch(() => {
      if (active) setEventTypesUnavailable(true);
    });
    return () => { active = false; };
  }, []);

  const filterChanged = () => { setCursor(""); setPrevious([]); };
  const changePeriod = (value: HistoryPeriod) => {
    setPeriod(value);
    setRangeAnchor(new Date());
    filterChanged();
  };
  const restartBrowse = () => {
    setRangeAnchor(new Date());
    setCursor("");
    setPrevious([]);
    setRefreshSequence(sequence => sequence + 1);
  };
  const clearFilters = () => { setPeriod({ kind: "7d" }); setRangeAnchor(new Date()); setSearch(""); setEventType(""); filterChanged(); };

  return <Stack spacing={2}>
    <HistoryFilters title={label("Filters", "Filtros")} language={pt ? "pt-BR" : "en"} onClear={clearFilters}>
    <PeriodControl value={period} timeZone={timeZone} maxDays={365} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ mt: 2, flexWrap: "wrap" }}>
      <TextField label={label("Search audit", "Buscar auditoria")} value={search} onChange={event => { setSearch(event.target.value); filterChanged(); }} slotProps={{ htmlInput: { maxLength: 100 } }} />
      <TextField select label={label("Event type", "Tipo de evento")} value={eventType} onChange={event => { setEventType(event.target.value); filterChanged(); }} sx={{ minWidth: 180 }}>
        <MenuItem value="">{label("All event types", "Todos os tipos")}</MenuItem>
        {eventTypes.map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}
      </TextField>
    </Stack>
    </HistoryFilters>
    <HistoryResults title={label("Results", "Resultados")} onRefresh={restartBrowse} refreshLabel={label("Refresh", "Atualizar")} updatedAt={updatedAt ? `${label("Updated", "Atualizado")}: ${updatedAt.toLocaleTimeString(locale, { timeZone })}` : undefined} footer={<CursorPager previousCount={previous.length} nextCursor={data?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
      onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
      onNext={() => { if (!data?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(data.nextCursor); }} />}>
    <Typography variant="caption" color="text.secondary" sx={{ display: { xs: "block", md: "none" }, mb: 1 }}>{label("Scroll horizontally to view all columns.", "Deslize horizontalmente para ver todas as colunas.")}</Typography>
    {eventTypesUnavailable && <Alert severity="warning">{label("Event type filters could not be loaded.", "Não foi possível carregar os tipos de evento.")}</Alert>}
    {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={restartBrowse}>{label("Retry", "Tentar novamente")}</Button>}>
      {label("Audit data could not be refreshed.", "Não foi possível atualizar a auditoria.")} {stale && label("Showing stale data.", "Exibindo dados desatualizados.")}
    </Alert>}
    {loading && !data ? <Typography>{label("Loading audit history…", "Carregando histórico de auditoria…")}</Typography> : !data ? <Alert severity="error">{label("Audit history is unavailable.", "Histórico de auditoria indisponível.")}</Alert> : <><TableContainer sx={{ width: "100%", overflowX: "auto" }}>
      <Table size="small" aria-label={label("Audit", "Auditoria")} sx={{ minWidth: 800, "& td": { py: 1.5 } }}>
        <TableHead><TableRow>{[label("Time", "Horário"), label("Action", "Ação"), label("Actor", "Ator"), label("Mode", "Modo"), label("Correlation", "Correlação")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
        <TableBody>
          {auditItems.map(item => <TableRow key={item.eventId}>
            <TableCell>{new Date(item.occurredAtUtc).toLocaleString(locale, { timeZone })}</TableCell>
            <TableCell><StatusBadge label={item.eventType} tone="default" /></TableCell>
            <TableCell>{item.actorSubject}</TableCell>
            <TableCell>{item.authenticationMode}</TableCell>
            <TableCell>{item.correlationId}</TableCell>
          </TableRow>)}
        </TableBody>
      </Table>
    </TableContainer>{auditItems.length === 0 && <EmptyState title={label("No audit events match this filter.", "Nenhum evento de auditoria corresponde a este filtro.")} />}</>}
    </HistoryResults>
  </Stack>;
}
