export type DynamicValueSource = "ReferenceDate" | "ScheduledTimestamp" | "FirstExecutionStart" | "ExecutionId" | "IdempotencyKey";
export type DynamicValueBoundary = "None" | "StartOfDay" | "EndOfDay" | "StartOfMonth" | "EndOfMonth" | "StartOfYear" | "EndOfYear";
export type DynamicValueFormat = "String" | "IsoDate" | "IsoOffsetTimestamp" | "IsoUtcTimestamp" | "UnixSeconds" | "UnixMilliseconds";

export interface DynamicValueBinding {
  target: string;
  source: DynamicValueSource;
  timeZoneId: string | null;
  months: number;
  days: number;
  boundary: DynamicValueBoundary;
  format: DynamicValueFormat;
}

export interface DynamicValuePlan {
  resolverVersion: 1;
  timeZoneId: string;
  bindings: DynamicValueBinding[];
}

export const emptyDynamicValuePlan = (timeZoneId = "UTC"): DynamicValuePlan => ({ resolverVersion: 1, timeZoneId, bindings: [] });
