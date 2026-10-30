import { describe, expect, it } from "vitest";
import { changedJobSections, createJobDefinitionDraft } from "./JobDefinitionDraft";

describe("job definition draft", () => {
  it("keeps complete saved bindings, profile, execution policy, and temporal values", () => {
    const source = {
      revision: { name: "Long lived", profileId: "profile-1", policy: { attempts: 7 }, configuration: { value: "saved" }, dynamicValues: { bindings: [{ source: "scheduledAt" }] }, hideFailureDetails: true },
      schedule: { kind: "Once", zoneId: "America/Sao_Paulo", localOnce: "2026-11-01T01:30:00", selectedOffset: "-03:00:00.0000001" },
      missedPolicy: { mode: "CatchUp", tolerance: "PT6H" },
    };
    const draft = createJobDefinitionDraft(source);
    expect(draft).toEqual(source);
    expect(changedJobSections(source, draft)).toEqual([]);
    draft.revision.name = "Renamed";
    expect(changedJobSections(source, draft)).toEqual(["configuration"]);
    expect(draft.schedule).toEqual(source.schedule);
    expect(draft.missedPolicy).toEqual(source.missedPolicy);
  });
  it("defaults an older draft to visible failure details", () => {
    const source = { revision: { name: "Legacy" }, schedule: null, missedPolicy: null };
    const draft = createJobDefinitionDraft(source);
    expect((draft.revision as Record<string, unknown>).hideFailureDetails).toBe(false);
    expect(source.revision).not.toHaveProperty("hideFailureDetails");
  });
});
