export interface DashboardDto {
  instanceId: string; observedUtc: string; overall: string; queued: number; running: number; needsReview: number;
  succeeded24h: number; failed24h: number; longRunning: number; upcoming: number; readyWorkers: number; totalWorkers: number;
  desiredCapacityRevision: number; effectiveCapacityRevision: number; capacityState: string; pendingProjections: number;
  upcomingItems: { jobId: string; jobName: string; templateId: string; scheduledForUtc: string; state: string }[];
  recentItems: { executionId: string; jobId: string; jobName: string; state: string; createdAtUtc: string; correlationId: string }[];
}
export interface MonitoringPage<T> { items: T[]; nextCursor: string | null }
export interface JobSummaryDto { jobId: string; revision: number; name: string; templateId: string; templateVersion: string; enabled: boolean; archived: boolean; hasSchedule: boolean; schedulePaused: boolean; activeExecutions: number; failed24h: number; createdAtUtc: string }
export interface WorkerSummaryDto { installationId: string; state: string; capacityCeiling: number; liveLimit: number; acknowledgedRevision: number; lastHeartbeatUtc: string; reservationsRecovered: boolean; capabilities: string[] }
export interface ExecutionDetailsDto { executionId: string; jobId: string; jobName: string; templateId: string; state: string; certainty: string; triggerKind: string; createdAtUtc: string; availableAtUtc: string; scheduledForUtc: string | null; correlationId: string; stopRequested: boolean; forceStop: boolean; attempts: { attemptId: string; attemptNumber: number; workerId: string | null; state: string; startedAtUtc: string | null; endedAtUtc: string | null; deadlineUtc: string | null; safeCode: string | null; localExitConfirmed: boolean; effectsMayHaveOccurred: boolean }[]; timeline: { eventId: string; attemptId: string | null; state: string; occurredAtUtc: string }[] }
export interface AuditSummaryDto { eventId: string; eventType: string; actorSubject: string; authenticationMode: string; correlationId: string; occurredAtUtc: string }
