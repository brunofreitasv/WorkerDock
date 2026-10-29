import { useEffect, useMemo, useState } from "react";
import { Alert, Button, Checkbox, FormControlLabel, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography } from "@mui/material";
import { read } from "../../api/client";
import { CursorPager } from "../monitoring/CursorPager";
import { PeriodControl } from "../monitoring/PeriodControl";
import { toUtcRange, type HistoryPeriod } from "../monitoring/historyTime";
import { useVisiblePolling } from "../monitoring/useVisiblePolling";
import type { MonitoringPage } from "../monitoring/monitoringTypes";

type Incident = { incidentId: string; ruleId: string; ruleName: string; condition: string; subjectKey: string; state: string; openedAtUtc: string; lastObservedAtUtc: string; resolvedAtUtc: string | null; safeCode: string | null };
type RuleOption = { ruleId: string; name: string };

function displayDate(value: string | null, locale: string, timeZone: string) {
  return value ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date(value)) : "—";
}

export function IncidentHistoryTable({ language, timeZone }: { language: string; timeZone: string }) {
  const pt = language === "pt-BR"; const locale = pt ? "pt-BR" : "en";
  const label = (en: string, br: string) => pt ? br : en;
  const [state, setState] = useState("Active");
  const [ruleId, setRuleId] = useState("");
  const [period, setPeriod] = useState<HistoryPeriod>({ kind: "30d" });
  const [activePeriodEnabled, setActivePeriodEnabled] = useState(false);
  const [anchor, setAnchor] = useState(() => new Date());
  const [cursor, setCursor] = useState(""); const [previous, setPrevious] = useState<string[]>([]); const [refreshSequence, setRefreshSequence] = useState(0);
  const [rules, setRules] = useState<RuleOption[]>([]);
  useEffect(() => { let active = true; void read<MonitoringPage<RuleOption>>("/api/alerts/rule-options?limit=100").then(x => { if (active) setRules(x.data.items); }).catch(() => undefined); return () => { active = false; }; }, []);
  const range = useMemo(() => toUtcRange(period, timeZone, anchor, 90), [period, timeZone, anchor]);
  const params = new URLSearchParams({ limit: "50", state });
  if (state !== "Active" || activePeriodEnabled) { params.set("fromUtc", range.fromUtc); params.set("toUtc", range.toUtc); }
  if (ruleId) params.set("ruleId", ruleId);
  if (cursor) params.set("cursor", cursor);
  const url = `/api/alerts/incidents?${params.toString()}`;
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(
    () => read<MonitoringPage<Incident>>(url).then(x => x.data), `${url}:${refreshSequence}`,
  );
  const reset = () => { setCursor(""); setPrevious([]); };
  const changePeriod = (value: HistoryPeriod) => { setPeriod(value); setActivePeriodEnabled(true); setAnchor(new Date()); reset(); };
  const toggleActivePeriod = (enabled: boolean) => { setActivePeriodEnabled(enabled); setAnchor(new Date()); reset(); };
  const restart = () => { setAnchor(new Date()); reset(); setRefreshSequence(x => x + 1); };
  const stateLabel = (value: string) => value === "Active" ? label("Open and unknown", "Abertos e desconhecidos") : value === "Resolved" ? label("Resolved", "Resolvidos") : label("All incidents", "Todos os incidentes");
  const displayState = (value: string) => value === "Open" ? label("Open", "Aberto") : value === "Unknown" ? label("Unknown", "Desconhecido") : label("Resolved", "Resolvido");

  return <Paper variant="outlined" sx={{ p: 3 }}>
    <Stack spacing={2}>
      <Typography component="h2" variant="h6">{label("Incidents", "Incidentes")}</Typography>
      <Stack spacing={1}>
        <PeriodControl value={period} timeZone={timeZone} maxDays={90} language={pt ? "pt-BR" : "en"} onChange={changePeriod} />
        {state === "Active" && <FormControlLabel control={<Checkbox checked={activePeriodEnabled} onChange={event => toggleActivePeriod(event.target.checked)} />}
          label={label("Filter active incidents by period", "Filtrar incidentes ativos por período")} />}
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
          <TextField select label={label("Incident state", "Estado do incidente")} value={state} onChange={event => { setState(event.target.value); reset(); }}>
            {["Active", "All", "Resolved"].map(value => <MenuItem key={value} value={value}>{stateLabel(value)}</MenuItem>)}
          </TextField>
          <TextField select label={label("Incident rule", "Regra do incidente")} value={ruleId} onChange={event => { setRuleId(event.target.value); reset(); }}>
            <MenuItem value="">{label("All rules", "Todas as regras")}</MenuItem>{rules.map(rule => <MenuItem key={rule.ruleId} value={rule.ruleId}>{rule.name}</MenuItem>)}
          </TextField>
          <Button onClick={restart}>{label("Refresh", "Atualizar")}</Button>
          <Typography variant="caption" sx={{ alignSelf: "center" }}>{label("Updated", "Atualizado")}: {updatedAt?.toLocaleTimeString(locale, { timeZone })}</Typography>
        </Stack>
      </Stack>
      {error && <Alert severity="warning" action={<Button color="inherit" size="small" onClick={restart}>{label("Retry", "Tentar novamente")}</Button>}>
        {label("Incident history could not be refreshed.", "Não foi possível atualizar o histórico de incidentes.")} {stale && label("Showing stale results.", "Exibindo resultados desatualizados.")}
      </Alert>}
      {loading && !data ? <Typography>{label("Loading incidents…", "Carregando incidentes…")}</Typography> : !data ? <Alert severity="error">{label("Incident history is unavailable.", "Histórico de incidentes indisponível.")}</Alert> : <TableContainer sx={{ width: "100%", overflowX: "auto" }}>
        <Table size="small" aria-label={label("Incidents", "Incidentes")}>
          <TableHead><TableRow>{[label("Opened", "Aberto em"), label("Incident rule", "Regra do incidente"), label("Subject", "Assunto"), label("State", "Estado"), label("Last observed", "Última observação"), label("Resolved", "Resolvido em"), label("Safe code", "Código seguro")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow></TableHead>
          <TableBody>{data.items.map(item => <TableRow key={item.incidentId}>
            <TableCell>{displayDate(item.openedAtUtc, locale, timeZone)}</TableCell><TableCell>{item.ruleName}</TableCell><TableCell>{item.subjectKey}</TableCell><TableCell>{displayState(item.state)}</TableCell>
            <TableCell>{displayDate(item.lastObservedAtUtc, locale, timeZone)}</TableCell><TableCell>{displayDate(item.resolvedAtUtc, locale, timeZone)}</TableCell><TableCell>{item.safeCode ?? "—"}</TableCell>
          </TableRow>)}{data.items.length === 0 && <TableRow><TableCell colSpan={7}>{label("No incidents found.", "Nenhum incidente encontrado.")}</TableCell></TableRow>}</TableBody>
        </Table>
      </TableContainer>}
      <CursorPager previousCount={previous.length} nextCursor={data?.nextCursor ?? null} language={pt ? "pt-BR" : "en"}
        onPrevious={() => { const stack = [...previous]; setCursor(stack.pop() ?? ""); setPrevious(stack); }}
        onNext={() => { if (!data?.nextCursor) return; setPrevious(stack => [...stack, cursor]); setCursor(data.nextCursor); }} />
    </Stack>
  </Paper>;
}
