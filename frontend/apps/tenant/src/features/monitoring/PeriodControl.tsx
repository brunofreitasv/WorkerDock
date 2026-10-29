import { useEffect, useState } from "react";
import { Button, ButtonGroup, Stack, TextField } from "@mui/material";
import type { HistoryPeriod } from "./historyTime";
import { toUtcRange } from "./historyTime";

type Language = "en" | "pt-BR";
type Props = { value: HistoryPeriod; timeZone: string; maxDays: number; language: Language; onChange: (value: HistoryPeriod) => void };

const labels = {
  en: { group: "History period", presets: { "24h": "Last 24 hours", "7d": "Last 7 days", "30d": "Last 30 days" }, custom: "Custom", start: "Start date", end: "End date" },
  "pt-BR": { group: "Período do histórico", presets: { "24h": "Últimas 24 horas", "7d": "Últimos 7 dias", "30d": "Últimos 30 dias" }, custom: "Personalizado", start: "Data inicial", end: "Data final" },
} satisfies Record<Language, { group: string; presets: Record<"24h" | "7d" | "30d", string>; custom: string; start: string; end: string }>;

export function PeriodControl({ value, timeZone, maxDays, language, onChange }: Props) {
  const text = labels[language];
  const [customSelected, setCustomSelected] = useState(value.kind === "custom");
  const [draft, setDraft] = useState(() => value.kind === "custom" ? value : { kind: "custom" as const, startDate: "", endDate: "" });
  useEffect(() => {
    if (value.kind === "custom") {
      setDraft(value);
      setCustomSelected(true);
    } else {
      setCustomSelected(false);
    }
  }, [value.kind, value.kind === "custom" ? value.startDate : "", value.kind === "custom" ? value.endDate : ""]);

  const changeDate = (field: "startDate" | "endDate", date: string) => {
    const candidate = { ...draft, [field]: date };
    setDraft(candidate);
    if (!candidate.startDate || !candidate.endDate) return;
    try {
      toUtcRange(candidate, timeZone, new Date(), maxDays);
      onChange(candidate);
    } catch {
      // Keep the current valid filter until both dates form a valid range.
    }
  };

  return <Stack spacing={1}>
    <ButtonGroup aria-label={text.group}>
      {(["24h", "7d", "30d"] as const).map(kind => <Button key={kind} aria-pressed={value.kind === kind} onClick={() => { setCustomSelected(false); onChange({ kind }); }}>{text.presets[kind]}</Button>)}
      <Button aria-pressed={customSelected} onClick={() => setCustomSelected(true)}>{text.custom}</Button>
    </ButtonGroup>
    <Stack direction="row" spacing={1}>
      <TextField label={text.start} type="date" value={draft.startDate} onChange={event => changeDate("startDate", event.target.value)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { max: draft.endDate || undefined } }} />
      <TextField label={text.end} type="date" value={draft.endDate} onChange={event => changeDate("endDate", event.target.value)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: draft.startDate || undefined } }} />
    </Stack>
  </Stack>;
}
