export interface JobDefinitionDraft {
  revision: Record<string, unknown>;
  schedule: Record<string, unknown> | null;
  missedPolicy: Record<string, unknown> | null;
}

export function createJobDefinitionDraft<T extends JobDefinitionDraft>(source: T): T {
  const draft = structuredClone(source);
  draft.revision.hideFailureDetails ??= false;
  return draft;
}

export function changedJobSections(original: JobDefinitionDraft, draft: JobDefinitionDraft): string[] {
  const changed: string[] = [];
  if (JSON.stringify(original.revision) !== JSON.stringify(draft.revision)) changed.push("configuration");
  if (JSON.stringify(original.schedule) !== JSON.stringify(draft.schedule)) changed.push("schedule");
  if (JSON.stringify(original.missedPolicy) !== JSON.stringify(draft.missedPolicy)) changed.push("policy");
  return changed;
}
