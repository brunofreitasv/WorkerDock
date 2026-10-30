export type JobView = "current" | "enabled" | "disabled" | "archived";

export interface JobListItem {
  jobId: string;
  name: string;
  templateId: string;
  templateVersion: string;
  enabled: boolean;
  archived: boolean;
  scheduleKind: string | null;
  scheduleZoneId: string | null;
  schedulePaused: boolean;
  configurationUpdatedUtc: string;
}

export interface JobPage {
  items: JobListItem[];
  nextCursor: string | null;
}
