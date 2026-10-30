import { Alert, Button, Card, CardContent, Grid, Link, Stack, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { DashboardDto } from "./monitoringTypes";
import { formatDuration } from "./historyTime";
import { SectionSurface, StatusBadge } from "@workerdock/ui";

export function Dashboard({ language, timeZone, onHistory, onExecution }: { language: string; timeZone: string; onHistory(): void; onExecution(id: string): void }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt, refresh } = useVisiblePolling(() => read<DashboardDto>("/api/dashboard").then(x => x.data));
  const label = (en: string, br: string) => pt ? br : en;
  if (loading && !data) return <Typography>{label("Loading monitoring data…", "Carregando dados de monitoramento…")}</Typography>;
  return <Stack spacing={2}>
    {error && <Alert severity="warning">{label("Monitoring data is unavailable.", "Dados de monitoramento indisponíveis.")} {stale && label("Showing the last successful refresh.", "Exibindo a última atualização bem-sucedida.")}</Alert>}
    {data && <>
      <SectionSurface title={label("Current health", "Saúde atual")} actions={<Button onClick={refresh}>{label("Refresh", "Atualizar")}</Button>}>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={2} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between", mb: 2 }}><Stack direction="row" spacing={1} sx={{ alignItems: "center" }}><Typography>{label("Status", "Estado")}:</Typography><StatusBadge label={data.overall} tone={data.overall === "Healthy" ? "success" : "warning"} /></Stack><Typography variant="caption">{label("Updated", "Atualizado")}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en", { timeZone })}</Typography></Stack>
        <Grid container spacing={2}>{[[label("Runtime", "Runtime"), data.runtimeReady ? label("Ready", "Pronto") : label("Unavailable", "Indisponível")], [label("Host capacity", "Capacidade do host"), data.hostCeiling]].map(([title, value]) => <Grid key={String(title)} size={{ xs: 6 }}><Card variant="outlined"><CardContent><Typography color="text.secondary">{title}</Typography><Typography variant="h5">{value}</Typography></CardContent></Card></Grid>)}</Grid>
      </SectionSurface>
      <SectionSurface title={label("Open items", "Itens em aberto")}><Grid container spacing={2}>{[[label("Needs review", "Aguardando revisão"), data.needsReview], [label("Queued", "Na fila"), data.queued], [label("Running", "Em execução"), data.running], [label("Long running", "Longa duração"), data.longRunning]].map(([title, value]) => <Grid key={String(title)} size={{ xs: 6, md: 3 }}><Card variant="outlined"><CardContent><Typography color="text.secondary">{title}</Typography><Typography variant="h5">{value}</Typography></CardContent></Card></Grid>)}</Grid></SectionSurface>
      <SectionSurface title={label("24-hour activity", "Atividade em 24 horas")}><Grid container spacing={2}>{[[label("Succeeded 24h", "Sucesso 24h"), data.succeeded24h], [label("Failed 24h", "Falhas 24h"), data.failed24h]].map(([title, value]) => <Grid key={String(title)} size={{ xs: 6 }}><Card variant="outlined"><CardContent><Typography color="text.secondary">{title}</Typography><Typography variant="h5">{value}</Typography></CardContent></Card></Grid>)}</Grid></SectionSurface>
      <SectionSurface title={label("Recent executions", "Execuções recentes")} actions={<Button onClick={onHistory}>{label("View all executions", "Ver todas as execuções")}</Button>}>
          <TableContainer sx={{ width: "100%", overflowX: "auto" }}>
            <Table size="small" aria-label={label("Recent executions", "Execuções recentes")}>
              <TableHead>
                <TableRow>{[label("Job", "Tarefa"), label("Template", "Modelo"), label("Status", "Estado"), label("Created", "Criada"), label("Duration", "Duração")].map(column => <TableCell key={column}>{column}</TableCell>)}</TableRow>
              </TableHead>
              <TableBody>
                {(data.recentItems ?? []).slice(0, 10).map(item => (
                  <TableRow key={item.executionId}>
                    <TableCell><Link component="button" onClick={() => onExecution(item.executionId)}>{item.jobName}</Link></TableCell>
                    <TableCell>{item.templateId}</TableCell>
                    <TableCell>{item.state}</TableCell>
                    <TableCell>{new Date(item.createdAtUtc).toLocaleString(pt ? "pt-BR" : "en", { timeZone })}</TableCell>
                    <TableCell>{formatDuration(item.duration)}</TableCell>
                  </TableRow>
                ))}
                {(data.recentItems ?? []).length === 0 && <TableRow><TableCell colSpan={5}>{label("No recent executions.", "Nenhuma execução recente.")}</TableCell></TableRow>}
              </TableBody>
            </Table>
          </TableContainer>
      </SectionSurface>
      <SectionSurface title={label("Upcoming work", "Próximas tarefas")}>{(data.upcomingItems ?? []).length ? data.upcomingItems.map(item => <Typography key={item.jobId} sx={{ py: 1, borderBottom: "1px solid", borderColor: "divider" }}>{item.jobName} · {item.templateId} · {new Date(item.scheduledForUtc).toLocaleString(pt ? "pt-BR" : "en", { timeZone })}</Typography>) : <Typography>{label("No upcoming one-time schedules.", "Nenhum agendamento único próximo.")}</Typography>}</SectionSurface>
    </>}
  </Stack>;
}
