import { Alert, Card, CardContent, Stack, Typography } from "@mui/material";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { ExecutionDetailsDto } from "./monitoringTypes";

export function ExecutionDetails({ executionId, language }: { executionId: string; language: string }) {
  const pt = language === "pt-BR";
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(() => read<ExecutionDetailsDto>(`/api/monitoring/executions/${encodeURIComponent(executionId)}`).then(x => x.data), executionId);
  if (loading && !data) return <Typography>{pt ? "Carregando execução…" : "Loading execution…"}</Typography>;
  if (!data) return <Alert severity="error">{pt ? "Detalhes da execução indisponíveis." : "Execution details are unavailable."}</Alert>;
  return <Stack spacing={2}>
    {error && <Alert severity="warning">{pt ? "A atualização falhou." : "Refresh failed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}
    <Card variant="outlined"><CardContent><Typography variant="h6">{data.jobName} · {data.state}</Typography><Typography>{pt ? "Execução" : "Execution"}: {data.executionId}</Typography><Typography>{pt ? "Modelo" : "Template"}: {data.templateId} · {data.triggerKind}</Typography><Typography>{pt ? "Criada" : "Created"}: {new Date(data.createdAtUtc).toLocaleString(pt ? "pt-BR" : "en")}</Typography><Typography>{pt ? "Correlação" : "Correlation"}: {data.correlationId}</Typography><Typography>{pt ? "Certeza" : "Certainty"}: {data.certainty}</Typography>{data.stopRequested && <Typography>{data.forceStop ? (pt ? "Parada forçada solicitada; o resultado remoto pode ser desconhecido." : "Force stop requested; remote outcome may be unknown.") : (pt ? "Parada solicitada." : "Stop requested.")}</Typography>}<Typography variant="caption">{pt ? "Atualizado" : "Updated"}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography></CardContent></Card>
    <Card variant="outlined"><CardContent><Typography variant="h6">{pt ? "Tentativas" : "Attempts"}</Typography>{data.attempts.length ? data.attempts.map(attempt => <Typography key={attempt.attemptId}>{pt ? "Tentativa" : "Attempt"} {attempt.attemptNumber} · {attempt.state} · {attempt.safeCode ?? "—"} · {attempt.startedAtUtc ? new Date(attempt.startedAtUtc).toLocaleString(pt ? "pt-BR" : "en") : (pt ? "Não iniciada" : "Not started")} {attempt.effectsMayHaveOccurred && `· ${pt ? "Efeitos podem ter ocorrido" : "Effects may have occurred"}`}</Typography>) : <Typography>{pt ? "Sem tentativas." : "No attempts."}</Typography>}</CardContent></Card>
    <Card variant="outlined"><CardContent><Typography variant="h6">{pt ? "Linha do tempo" : "Timeline"}</Typography>{data.timeline.map(item => <Typography key={item.eventId}>{new Date(item.occurredAtUtc).toLocaleString(pt ? "pt-BR" : "en")} · {item.state}</Typography>)}</CardContent></Card>
  </Stack>;
}
