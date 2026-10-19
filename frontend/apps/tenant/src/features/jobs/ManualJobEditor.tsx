import type { Labels } from "@workerdock/ui";
import type { Language } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";

export function ManualJobEditor({ t, defaultZone, language, setLanguage }: { t: Labels; defaultZone?: string; language?: Language; setLanguage?: (value: Language) => void }) {
  return <JobWizard t={t} defaultZone={defaultZone} language={language} setLanguage={setLanguage} />;
}
