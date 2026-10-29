export interface DashboardDto {
  instanceId: string; observedUtc: string; overall: string; queued: number; running: number; needsReview: number;
  succeeded24h: number; failed24h: number; longRunning: number; upcoming: number; runtimeReady: boolean; hostCeiling: number; runtimeHeartbeatUtc: string | null;
  upcomingItems: { jobId: string; jobName: string; templateId: string; scheduledForUtc: string; state: string }[];
  recentItems: { executionId: string; jobId: string; jobName: string; state: string; createdAtUtc: string; correlationId: string; templateId: string; duration: string | null }[];
}
export interface MonitoringPage<T> { items: T[]; nextCursor: string | null }
export interface ExecutionHistoryItemDto { executionId: string; jobId: string; jobName: string; templateId: string; state: string; triggerKind: string; createdAtUtc: string; startedAtUtc: string | null; duration: string | null }
export interface JobSummaryDto { jobId: string; revision: number; name: string; templateId: string; templateVersion: string; enabled: boolean; archived: boolean; hasSchedule: boolean; schedulePaused: boolean; activeExecutions: number; failed24h: number; createdAtUtc: string }
export interface RuntimeStatusDto { bootId: string | null; ready: boolean; hostCeiling: number; lastHeartbeatUtc: string | null }
export interface ExecutionDetailsDto { executionId: string; jobId: string; jobName: string; templateId: string; state: string; certainty: string; triggerKind: string; createdAtUtc: string; availableAtUtc: string; scheduledForUtc: string | null; correlationId: string; stopRequested: boolean; forceStop: boolean; attempts: { attemptId: string; attemptNumber: number; workerId: string | null; state: string; startedAtUtc: string | null; endedAtUtc: string | null; deadlineUtc: string | null; safeCode: string | null; localExitConfirmed: boolean; effectsMayHaveOccurred: boolean }[]; timeline: { eventId: string; attemptId: string | null; state: string; occurredAtUtc: string }[] }
export interface AuditSummaryDto { eventId: string; eventType: string; actorSubject: string; authenticationMode: string; correlationId: string; occurredAtUtc: string }
