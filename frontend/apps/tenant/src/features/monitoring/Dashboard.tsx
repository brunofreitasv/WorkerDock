import { Alert, Button, Card, CardContent, Grid, Link, Stack, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { DashboardDto } from "./monitoringTypes";

export function Dashboard({ language, onExecution }: { language: string; onExecution(id: string): void }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt, refresh } = useVisiblePolling(() => read<DashboardDto>("/api/dashboard").then(x => x.data));
  const label = (en: string, br: string) => pt ? br : en;
  if (loading && !data) return <Typography>{label("Loading monitoring data…", "Carregando dados de monitoramento…")}</Typography>;
  return <Stack spacing={2}>
    {error && <Alert severity="warning">{label("Monitoring data is unavailable.", "Dados de monitoramento indisponíveis.")} {stale && label("Showing the last successful refresh.", "Exibindo a última atualização bem-sucedida.")}</Alert>}
    {data && <>
      <Stack direction="row" spacing={1} sx={{ alignItems: "center" }}><Typography>{label("Status", "Estado")}: <strong>{data.overall}</strong></Typography><Button onClick={refresh}>{label("Refresh", "Atualizar")}</Button><Typography variant="caption">{label("Updated", "Atualizado")}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography></Stack>
      <Grid container spacing={2}>{[
        [label("Queued", "Na fila"), data.queued], [label("Running", "Em execução"), data.running], [label("Needs review", "Aguardando revisão"), data.needsReview],
        [label("Succeeded 24h", "Sucesso 24h"), data.succeeded24h], [label("Failed 24h", "Falhas 24h"), data.failed24h], [label("Long running", "Longa duração"), data.longRunning],
        [label("Runtime", "Runtime"), data.runtimeReady ? label("Ready", "Pronto") : label("Unavailable", "Indisponível")], [label("Host capacity", "Capacidade do host"), data.hostCeiling],
      ].map(([title, value]) => <Grid key={String(title)} size={{ xs: 6, md: 3 }}><Card variant="outlined"><CardContent><Typography color="text.secondary">{title}</Typography><Typography variant="h5">{value}</Typography></CardContent></Card></Grid>)}</Grid>
      <Card variant="outlined"><CardContent><Typography variant="h6">{label("Recent executions", "Execuções recentes")}</Typography>{(data.recentItems ?? []).length ? data.recentItems.map(item => <Typography key={item.executionId}><Link component="button" onClick={() => onExecution(item.executionId)}>{item.jobName} · {item.state} · {new Date(item.createdAtUtc).toLocaleString(pt ? "pt-BR" : "en")}</Link></Typography>) : <Typography>{label("No recent executions.", "Nenhuma execução recente.")}</Typography>}</CardContent></Card>
      <Card variant="outlined"><CardContent><Typography variant="h6">{label("Upcoming work", "Próximas tarefas")}</Typography>{(data.upcomingItems ?? []).length ? data.upcomingItems.map(item => <Typography key={item.jobId}>{item.jobName} · {item.templateId} · {new Date(item.scheduledForUtc).toLocaleString(pt ? "pt-BR" : "en")}</Typography>) : <Typography>{label("No upcoming one-time schedules.", "Nenhum agendamento único próximo.")}</Typography>}</CardContent></Card>
    </>}
  </Stack>;
}
