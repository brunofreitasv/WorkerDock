import { Button, ButtonGroup, Stack, TextField } from "@mui/material";
import type { HistoryPeriod } from "./historyTime";
import { toUtcRange } from "./historyTime";

type Props = { value: HistoryPeriod; timeZone: string; maxDays: number; onChange: (value: HistoryPeriod) => void };

const presets = [
  { kind: "24h", label: "Last 24 hours" },
  { kind: "7d", label: "Last 7 days" },
  { kind: "30d", label: "Last 30 days" },
] as const;

export function PeriodControl({ value, timeZone, maxDays, onChange }: Props) {
  const custom = value.kind === "custom" ? value : { kind: "custom" as const, startDate: "", endDate: "" };
  const changeDate = (field: "startDate" | "endDate", date: string) => {
    const candidate = { ...custom, [field]: date };
    if (!candidate.startDate || !candidate.endDate) return;
    try {
      toUtcRange(candidate, timeZone, new Date(), maxDays);
      onChange(candidate);
    } catch {
      // Keep the current valid filter until both dates form a valid range.
    }
  };

  return <Stack spacing={1}>
    <ButtonGroup aria-label="History period">
      {presets.map(preset => <Button key={preset.kind} aria-pressed={value.kind === preset.kind} onClick={() => onChange({ kind: preset.kind })}>{preset.label}</Button>)}
      <Button aria-pressed={value.kind === "custom"} onClick={() => onChange({ kind: "custom", startDate: custom.startDate, endDate: custom.endDate })}>Custom</Button>
    </ButtonGroup>
    <Stack direction="row" spacing={1}>
      <TextField label="Start date" type="date" value={custom.startDate} onChange={event => changeDate("startDate", event.target.value)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { max: custom.endDate || undefined } }} />
      <TextField label="End date" type="date" value={custom.endDate} onChange={event => changeDate("endDate", event.target.value)} slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: custom.startDate || undefined } }} />
    </Stack>
  </Stack>;
}
