import { useEffect, useState } from "react";
import { Button, ButtonGroup, Stack, TextField, Typography } from "@mui/material";
import type { HistoryPeriod } from "./historyTime";
import { toUtcRange } from "./historyTime";

type Language = "en" | "pt-BR";
type Props = { value: HistoryPeriod; timeZone: string; maxDays: number; language: Language; onChange: (value: HistoryPeriod) => void };

const labels = {
  en: { group: "History period", presets: { "24h": "Last 24 hours", "7d": "Last 7 days", "30d": "Last 30 days" }, custom: "Custom", start: "Start date", end: "End date", incomplete: "Enter both dates to apply a custom period.", reversed: "Start date must not follow end date.", invalid: "Enter valid calendar dates.", limit: (days: number) => `Choose a range of ${days} days or fewer.` },
  "pt-BR": { group: "Período do histórico", presets: { "24h": "Últimas 24 horas", "7d": "Últimos 7 dias", "30d": "Últimos 30 dias" }, custom: "Personalizado", start: "Data inicial", end: "Data final", incomplete: "Informe as duas datas para aplicar um período personalizado.", reversed: "A data inicial não pode ser posterior à final.", invalid: "Informe datas válidas.", limit: (days: number) => `Escolha um período de até ${days} dias.` },
} satisfies Record<Language, { group: string; presets: Record<"24h" | "7d" | "30d", string>; custom: string; start: string; end: string; incomplete: string; reversed: string; invalid: string; limit: (days: number) => string }>;

export function PeriodControl({ value, timeZone, maxDays, language, onChange }: Props) {
  const text = labels[language];
  const [customSelected, setCustomSelected] = useState(value.kind === "custom");
  const [draft, setDraft] = useState(() => value.kind === "custom" ? value : { kind: "custom" as const, startDate: "", endDate: "" });
  const [error, setError] = useState("");
  useEffect(() => {
    if (value.kind === "custom") {
      setDraft(value);
      setCustomSelected(true);
      setError("");
    } else {
      setCustomSelected(false);
      setError("");
    }
  }, [value.kind, value.kind === "custom" ? value.startDate : "", value.kind === "custom" ? value.endDate : ""]);

  const changeDate = (field: "startDate" | "endDate", date: string, badInput: boolean) => {
    const candidate = { ...draft, [field]: date };
    setDraft(candidate);
    if (badInput) {
      setError(text.invalid);
      return;
    }
    if (!candidate.startDate || !candidate.endDate) {
      setError(text.incomplete);
      return;
    }
    try {
      toUtcRange(candidate, timeZone, new Date(), maxDays);
      setError("");
      onChange(candidate);
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : "";
      setError(message.includes("must not follow") ? text.reversed : message.includes("exceeds maximum") ? text.limit(maxDays) : text.invalid);
    }
  };

  return <Stack spacing={1}>
    <ButtonGroup aria-label={text.group} sx={{ flexWrap: "wrap", gap: 0.5, boxShadow: "none" }}>
      {(["24h", "7d", "30d"] as const).map(kind => <Button key={kind} variant={!customSelected && value.kind === kind ? "contained" : "outlined"} aria-pressed={!customSelected && value.kind === kind} onClick={() => { setCustomSelected(false); setError(""); onChange({ kind }); }}>{text.presets[kind]}</Button>)}
      <Button variant={customSelected ? "contained" : "outlined"} aria-pressed={customSelected} onClick={() => setCustomSelected(true)}>{text.custom}</Button>
    </ButtonGroup>
    {customSelected && <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
      <TextField label={text.start} type="date" value={draft.startDate} onChange={event => changeDate("startDate", event.target.value, event.target.validity.badInput)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { max: draft.endDate || undefined } }} />
      <TextField label={text.end} type="date" value={draft.endDate} onChange={event => changeDate("endDate", event.target.value, event.target.validity.badInput)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: draft.startDate || undefined } }} />
    </Stack>}
    {customSelected && error && <Typography role="alert" color="error" variant="body2">{error}</Typography>}
  </Stack>;
}
