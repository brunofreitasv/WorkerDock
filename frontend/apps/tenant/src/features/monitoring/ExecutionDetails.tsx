import { Alert, Stack, Typography } from "@mui/material";
import { AttemptDiagnostic } from "./AttemptDiagnostic";
import { ExecutionReviewControls } from "./ExecutionReviewControls";
import { read } from "../../api/client";
import { useVisiblePolling } from "./useVisiblePolling";
import type { ExecutionDetailsDto } from "./monitoringTypes";
import { SectionSurface, StatusBadge, translations } from "@workerdock/ui";

export function ExecutionDetails({ executionId, language, canManage = false }: { executionId: string; language: string; canManage?: boolean }) {
  const pt = language === "pt-BR";
  const labels = translations[pt ? "pt-BR" : "en"];
  const waitReasonLabels = {
    ReviewHold: labels.executionWaitReasonReviewHold,
    ReviewSkip: labels.executionWaitReasonReviewSkip,
    EarlierQueued: labels.executionWaitReasonEarlierQueued,
    JobRunning: labels.executionWaitReasonJobRunning,
    JobCapacity: labels.executionWaitReasonJobCapacity,
    HostCapacity: labels.executionWaitReasonHostCapacity,
    WorkerUnavailable: labels.executionWaitReasonWorkerUnavailable,
    NotDue: labels.executionWaitReasonNotDue,
  };
  const { data, loading, error, stale, updatedAt } = useVisiblePolling(() => read<ExecutionDetailsDto>(`/api/monitoring/executions/${encodeURIComponent(executionId)}`).then(x => x.data), executionId);
  if (loading && !data) return <Typography>{pt ? "Carregando execução…" : "Loading execution…"}</Typography>;
  if (!data) return <Alert severity="error">{pt ? "Detalhes da execução indisponíveis." : "Execution details are unavailable."}</Alert>;
  return <Stack spacing={2}>
    {error && <Alert severity="warning">{pt ? "A atualização falhou." : "Refresh failed."} {stale && (pt ? "Exibindo dados desatualizados." : "Showing stale data.")}</Alert>}
    <SectionSurface title={pt ? "Visão geral da execução" : "Execution overview"}><Stack spacing={1}><Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}><Typography sx={{ fontWeight: 700 }}>{data.jobName}</Typography><StatusBadge label={data.state} tone={data.state === "Succeeded" ? "success" : data.state === "Failed" ? "error" : data.state === "NeedsReview" ? "warning" : "default"} /></Stack><Typography>{pt ? "Execução" : "Execution"}: {data.executionId}</Typography><Typography>{pt ? "Modelo" : "Template"}: {data.templateId} · {data.templateVersion ?? "—"} · {pt ? "Revisão do job" : "Job revision"}: {data.jobRevision ?? "—"} · {data.triggerKind}</Typography><Typography>{pt ? "Criada" : "Created"}: {new Date(data.createdAtUtc).toLocaleString(pt ? "pt-BR" : "en")}</Typography><Typography>{pt ? "Correlação" : "Correlation"}: {data.correlationId}</Typography><Typography>{pt ? "Certeza" : "Certainty"}: {data.certainty}</Typography>{data.stopRequested && <Typography>{data.forceStop ? (pt ? "Parada forçada solicitada; o resultado remoto pode ser desconhecido." : "Force stop requested; remote outcome may be unknown.") : (pt ? "Parada solicitada." : "Stop requested.")}</Typography>}{(data.state === "Queued" || data.state === "RetryWaiting") && data.waitReason && <Alert severity="info">{labels.executionWaitReasonTitle}: {waitReasonLabels[data.waitReason]}</Alert>}<Typography variant="caption">{pt ? "Atualizado" : "Updated"}: {updatedAt?.toLocaleTimeString(pt ? "pt-BR" : "en")}</Typography></Stack></SectionSurface>
    {data.state === "NeedsReview" && <SectionSurface title={pt ? "Revisão" : "Review"}><ExecutionReviewControls executionId={data.executionId} canManage={canManage} language={language} /></SectionSurface>}
    <SectionSurface title={pt ? "Tentativas" : "Attempts"}>{data.attempts.length ? data.attempts.map(attempt => <Stack key={attempt.attemptId} spacing={1} sx={{ mb: 2 }}><Typography>{pt ? "Tentativa" : "Attempt"} {attempt.attemptNumber} · {attempt.state} · {attempt.safeCode ?? "—"} · {attempt.startedAtUtc ? new Date(attempt.startedAtUtc).toLocaleString(pt ? "pt-BR" : "en") : (pt ? "Não iniciada" : "Not started")} {attempt.effectsMayHaveOccurred && `· ${pt ? "Efeitos podem ter ocorrido" : "Effects may have occurred"}`}</Typography><AttemptDiagnostic diagnostic={attempt.diagnostic} attemptId={attempt.attemptId} language={language} /></Stack>) : <Typography>{pt ? "Sem tentativas." : "No attempts."}</Typography>}</SectionSurface>
    <SectionSurface title={pt ? "Linha do tempo" : "Timeline"}>{data.timeline.map(item => <Typography key={item.eventId} sx={{ py: 1, borderBottom: "1px solid", borderColor: "divider" }}>{new Date(item.occurredAtUtc).toLocaleString(pt ? "pt-BR" : "en")} · {item.state === "SkippedReviewHold" ? labels.executionSkippedReviewHold : item.state}</Typography>)}</SectionSurface>
  </Stack>;
}
