import { render, screen, within, cleanup } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { ExecutionDetails } from "./ExecutionDetails";
import { useVisiblePolling } from "./useVisiblePolling";

vi.mock("./useVisiblePolling", () => ({ useVisiblePolling: vi.fn() }));
afterEach(cleanup);
function show(diagnostic: object | null, language = "en", state = "Failed", waitReason: string | null = null, timeline: { eventId: string; attemptId: string | null; state: string; occurredAtUtc: string }[] = []) {
  vi.mocked(useVisiblePolling).mockReturnValue({ data: { jobName: "Job", executionId: "execution", templateId: "sql", state, waitReason, createdAtUtc: "2026-10-29T00:00:00Z", attempts: [{ attemptId: "attempt", attemptNumber: 1, state: "Failed", safeCode: "CommandFailed", diagnostic }], timeline }, loading: false, error: null, stale: false, updatedAt: null } as never);
  return render(<ExecutionDetails executionId="execution" language={language} />);
}
test("expands bounded diagnostic text and SQL reproduction metadata safely", () => {
  const { container } = show({ kind: "Sql", exceptionType: "SqlException", message: "redacted [REDACTED]", stackTrace: "client stack", responseContent: '<img src=x onerror="alert(1)">', sqlNumber: 51000, sqlState: 1, sqlProcedure: "FailProc", sqlLine: 12, commandKind: "StoredProcedure", truncated: true });
  const detail = container.querySelector("details")!;
  expect(within(detail).getByText("Failure diagnostics")).toBeInTheDocument();
  expect(detail.open).toBe(false);
  expect(within(detail).getByText("redacted [REDACTED]")).toBeInTheDocument();
  expect(within(detail).getByText("client stack")).toBeInTheDocument();
  expect(within(detail).getByText("FailProc")).toBeInTheDocument();
  expect(within(detail).getByText("51000")).toBeInTheDocument();
  expect(within(detail).getByText("Output truncated")).toBeInTheDocument();
  expect(within(detail).getByText('<img src=x onerror="alert(1)">')).toBeInTheDocument();
  expect(detail.querySelector("img")).toBeNull();
});
test("hidden diagnostics retain safe metadata and never display detail fields", () => {
  show({ kind: "Http", hidden: true, message: "must not show", stackTrace: "private stack", responseContent: "private body", httpMethod: "POST", httpStatus: 500 });
  expect(screen.getByText("Details hidden by job setting")).toBeInTheDocument();
  expect(screen.getByText("500")).toBeInTheDocument();
  expect(screen.getByText("POST")).toBeInTheDocument();
  expect(screen.queryByText(/must not show|private stack|private body/)).toBeNull();
});
test("HTTP status failure labels missing stack unavailable", () => {
  show({ kind: "Http", httpStatus: 500, responseContent: "upstream failure" });
  expect(screen.getByText("Stack trace: Unavailable")).toBeInTheDocument();
  expect(screen.getByText("upstream failure")).toBeInTheDocument();
});
test("legacy attempts and Portuguese labels", () => {
  show(null, "pt-BR");
  expect(screen.getByText("Diagnóstico indisponível")).toBeInTheDocument();
});
test("Portuguese hidden and truncated labels use shared locales", () => {
  show({ kind: "Http", hidden: true, truncated: true }, "pt-BR");
  expect(screen.getByText("Diagnóstico da falha")).toBeInTheDocument();
  expect(screen.getByText("Detalhes ocultos pela configuração do job")).toBeInTheDocument();
  expect(screen.getByText("Saída truncada")).toBeInTheDocument();
});
test("missing SQL reproduction metadata is unavailable without irrelevant HTTP fields", () => {
  show({ kind: "Sql", hidden: true, sqlNumber: 51000 });
  for (const label of ["SQL state", "SQL procedure", "SQL line", "Command kind"])
    expect(screen.getByText(`${label}: Unavailable`)).toBeInTheDocument();
  expect(screen.queryByText(/HTTP method|HTTP status|Media type|Response bytes/)).toBeNull();
});
test("missing HTTP reproduction metadata is unavailable without irrelevant SQL fields", () => {
  show({ kind: "Http", hidden: true }, "pt-BR");
  for (const label of ["Método HTTP", "Status HTTP", "Tipo de conteúdo", "Bytes da resposta"])
    expect(screen.getByText(`${label}: Indisponível`)).toBeInTheDocument();
  expect(screen.queryByText(/Número SQL|Estado SQL|Procedimento SQL|Linha SQL|Tipo de comando/)).toBeNull();
});
test("queued execution shows a localized current wait reason and review skip event", () => {
  show(null, "pt-BR", "Queued", "JobCapacity", [{ eventId: "event", attemptId: null, state: "SkippedReviewHold", occurredAtUtc: "2026-10-29T00:00:00Z" }]);
  expect(screen.getByText("Motivo atual da espera ou do descarte: Limite de execuções do job atingido")).toBeInTheDocument();
  expect(screen.getByText(/Ignorada enquanto uma execução anterior aguarda revisão/)).toBeInTheDocument();
});
test("stored wait reason is not shown as historical fact for completed execution", () => {
  show(null, "en", "Succeeded", "HostCapacity");
  expect(screen.queryByText(/Current wait or skip reason|Motivo atual da espera ou do descarte/)).toBeNull();
});
test("queued Skip explains that review hold will skip the occurrence", () => {
  show(null, "en", "Queued", "ReviewSkip");
  expect(screen.getByText("Current wait or skip reason: Will be skipped while an earlier execution awaits review")).toBeInTheDocument();
});
test("Portuguese queued Skip explains that review hold will skip the occurrence", () => {
  show(null, "pt-BR", "Queued", "ReviewSkip");
  expect(screen.getByText("Motivo atual da espera ou do descarte: Será ignorada enquanto uma execução anterior aguarda revisão")).toBeInTheDocument();
});
