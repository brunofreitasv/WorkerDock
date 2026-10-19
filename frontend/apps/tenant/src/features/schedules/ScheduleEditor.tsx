import { useEffect, useState } from "react";
import { Alert, Button, MenuItem, Stack, TextField, Typography } from "@mui/material";
import { ApiError, read } from "../../api/client";
import type { Labels } from "@workerdock/ui";
import { SchedulePreview, type SchedulePreviewItemDto } from "./SchedulePreview";

export interface ScheduleEditorInputDto {
  kind: "Cron" | "Once";
  cron: string | null;
  zoneId: string;
  localOnce: string | null;
  selectedOffset: string | null;
}

export function ScheduleEditor(props: {
  value: ScheduleEditorInputDto;
  t: Labels;
  onChange(value: ScheduleEditorInputDto): void;
  preview(value: ScheduleEditorInputDto): Promise<SchedulePreviewItemDto[]>;
}) {
  const { value, onChange, preview, t } = props;
  const [zones, setZones] = useState<string[]>(["UTC"]);
  const [items, setItems] = useState<SchedulePreviewItemDto[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => { void read<string[]>("/api/time-zones").then(result => setZones(result.data)).catch(() => setZones(["UTC"])); }, []);
  const change = (patch: Partial<ScheduleEditorInputDto>) => { setItems([]); setError(""); onChange({ ...value, ...patch }); };
  const showPreview = async () => {
    setBusy(true); setError("");
    try { setItems(await preview(value)); }
    catch (cause) { setError(cause instanceof ApiError && cause.status === 412 ? "Schedule changed. Your draft is preserved." : "Unable to preview this schedule."); }
    finally { setBusy(false); }
  };
  return <Stack spacing={2}>
    <Typography variant="body2">{t.cronHelp}</Typography>
    <TextField select label={t.scheduleKind} value={value.kind} onChange={event => change({ kind: event.target.value as "Cron" | "Once", cron: event.target.value === "Cron" ? "0 9 * * 1-5" : null, localOnce: null, selectedOffset: null })}>
      <MenuItem value="Cron">{t.recurringSchedule}</MenuItem><MenuItem value="Once">{t.oneTimeSchedule}</MenuItem>
    </TextField>
    {value.kind === "Cron" ? <TextField label={t.cronExpression} value={value.cron ?? ""} onChange={event => change({ cron: event.target.value })} helperText={t.cronFieldsHelp} /> : <>
      <TextField label={t.localDateTime} type="datetime-local" value={value.localOnce ?? ""} onChange={event => change({ localOnce: event.target.value })} slotProps={{ inputLabel: { shrink: true } }} />
      <TextField label={t.utcOffset} value={value.selectedOffset ?? ""} onChange={event => change({ selectedOffset: event.target.value })} placeholder="-04:00" helperText={t.offsetHelp} />
      <TextField label={t.resolvedUtc} value={items[0]?.utc ?? ""} slotProps={{ input: { readOnly: true }, htmlInput: { "data-testid": "resolved-utc" } }} />
    </>}
    <TextField select label={t.scheduleTimeZone} value={value.zoneId} onChange={event => change({ zoneId: event.target.value })}>{zones.map(zone => <MenuItem key={zone} value={zone}>{zone}</MenuItem>)}</TextField>
    {error && <Alert severity="error" role="alert" tabIndex={-1}>{error === "Schedule changed. Your draft is preserved." ? t.staleDraftPreserved : t.previewError}</Alert>}
    <Button onClick={() => void showPreview()} disabled={busy || (value.kind === "Once" && (!value.localOnce || !value.selectedOffset))}>{t.previewSchedule}</Button>
    <SchedulePreview items={items} />
  </Stack>;
}
