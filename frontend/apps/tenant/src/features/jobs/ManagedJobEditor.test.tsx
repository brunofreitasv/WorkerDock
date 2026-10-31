import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { translations } from "@workerdock/ui";
import { changedJobSections, createJobDefinitionDraft } from "./JobDefinitionDraft";
import { ManagedJobEditor } from "./ManagedJobEditor";

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

it("defaults a newly added managed schedule to the client zone", async () => {
  const user = userEvent.setup();
  render(<ManagedJobEditor jobId="job-1" initial={{ revision: { name: "New schedule", configuration: {}, policy: {} }, schedule: null, missedPolicy: null }} etag='"1"' descriptor={{ id: "sample", version: "1", displayName: "Sample", description: "Sample", configurationSchema: { type: "object", properties: {} } }} t={translations.en} clientTimeZone="America/Sao_Paulo" onSaved={() => {}} onCancel={() => {}} />);
  await user.click(screen.getByRole("button", { name: translations.en.jobAddSchedule }));
  expect(screen.getByRole("combobox", { name: translations.en.scheduleTimeZone })).toHaveTextContent("America/Sao_Paulo");
});
