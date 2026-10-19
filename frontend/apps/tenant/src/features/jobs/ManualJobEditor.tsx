import type { Labels } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";

export function ManualJobEditor({ t, defaultZone }: { t: Labels; defaultZone?: string }) {
  return <JobWizard t={t} defaultZone={defaultZone} />;
}
