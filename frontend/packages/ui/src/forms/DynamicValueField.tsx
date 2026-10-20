import { MenuItem, Stack, TextField, Typography } from "@mui/material";
import type { Labels } from "../preferences";
import type { DynamicValueBinding, DynamicValueBoundary, DynamicValueFormat, DynamicValueSource } from "./dynamicValueTypes";

interface Props {
  binding?: DynamicValueBinding;
  onBindingChange(next: DynamicValueBinding | null): void;
  allowedTypes: string[];
  allowedSources?: DynamicValueSource[];
  allowedFormats?: DynamicValueFormat[];
  executionTimeZone: string;
  t: Labels;
  showMode?: boolean;
}

const sources: DynamicValueSource[] = ["ReferenceDate", "ScheduledTimestamp", "FirstExecutionStart", "ExecutionId", "IdempotencyKey"];
const boundaries: DynamicValueBoundary[] = ["None", "StartOfDay", "EndOfDay", "StartOfMonth", "EndOfMonth", "StartOfYear", "EndOfYear"];

export function DynamicValueField({ binding, onBindingChange, allowedTypes, allowedSources, allowedFormats, executionTimeZone, t, showMode = true }: Props) {
  const permittedSources = sources.filter(source => source !== "ReferenceDate" || allowedTypes.includes("string"))
    .filter(source => source !== "ExecutionId" && source !== "IdempotencyKey" || allowedTypes.includes("string"))
    .filter(source => !allowedSources || allowedSources.includes(source));
  const changeSource = (source: DynamicValueSource) => {
    const format: DynamicValueFormat = formatsForSource(source, allowedTypes).filter(item => !allowedFormats || allowedFormats.includes(item))[0] ?? "IsoDate";
    onBindingChange({ target: binding?.target ?? "", source, timeZoneId: source === "ReferenceDate" || source === "ExecutionId" || source === "IdempotencyKey" ? null : executionTimeZone, months: 0, days: 0, boundary: "None", format });
  };
  const formats = binding ? formatsForSource(binding.source, allowedTypes) : [];
  const availableFormats = allowedFormats ? formats.filter(format => allowedFormats.includes(format)) : formats;
  const update = (patch: Partial<DynamicValueBinding>) => binding && onBindingChange({ ...binding, ...patch });
  return <Stack spacing={1} sx={{ minWidth: 220 }}>
    {showMode && <TextField select label={t.dynamicValueSource} value={binding ? "dynamic" : "fixed"} onChange={event => {
      if (event.target.value === "fixed") onBindingChange(null);
      else changeSource(permittedSources[0] ?? "ReferenceDate");
    }}>
      <MenuItem value="fixed">{t.dynamicFixedValue}</MenuItem>
      {permittedSources.length > 0 && <MenuItem value="dynamic">{t.sqlDynamicValue}</MenuItem>}
    </TextField>}
    {binding && <>
      <TextField select label={t.dynamicSource} value={binding.source} onChange={event => changeSource(event.target.value as DynamicValueSource)}>
        {permittedSources.map(source => <MenuItem key={source} value={source}>{sourceLabel(t, source)}</MenuItem>)}
      </TextField>
      {binding.source !== "ReferenceDate" && binding.source !== "ExecutionId" && binding.source !== "IdempotencyKey" && <TextField label={t.dynamicTimeZone} value={binding.timeZoneId ?? executionTimeZone} onChange={event => update({ timeZoneId: event.target.value })} />}
      {!(["ExecutionId", "IdempotencyKey"].includes(binding.source)) && <>
        <Stack direction="row" spacing={1}>
          <TextField type="number" label={t.dynamicMonths} value={binding.months} onChange={event => update({ months: event.target.value === "" ? 0 : Number(event.target.value) })} slotProps={{ htmlInput: { min: -1200, max: 1200, step: 1 } }} />
          <TextField type="number" label={t.dynamicDays} value={binding.days} onChange={event => update({ days: event.target.value === "" ? 0 : Number(event.target.value) })} slotProps={{ htmlInput: { min: -36600, max: 36600, step: 1 } }} />
        </Stack>
        <TextField select label={t.dynamicBoundary} value={binding.boundary} onChange={event => update({ boundary: event.target.value as DynamicValueBoundary })}>
          {boundaries.map(boundary => <MenuItem key={boundary} value={boundary}>{boundaryLabel(t, boundary)}</MenuItem>)}
        </TextField>
      </>}
      <TextField select label={t.dynamicFormat} value={binding.format} onChange={event => update({ format: event.target.value as DynamicValueFormat })}>
        {availableFormats.map(format => <MenuItem key={format} value={format}>{formatLabel(format)}</MenuItem>)}
      </TextField>
      <Typography variant="caption" color="text.secondary">{t.dynamicExampleOnly}</Typography>
    </>}
  </Stack>;
}

function sourceLabel(t: Labels, source: DynamicValueSource): string {
  return ({ ReferenceDate: t.dynamicReferenceDate, ScheduledTimestamp: t.dynamicScheduledTimestamp, FirstExecutionStart: t.dynamicFirstExecutionStart, ExecutionId: t.dynamicExecutionId, IdempotencyKey: t.dynamicIdempotencyKey })[source];
}
function boundaryLabel(t: Labels, boundary: DynamicValueBoundary): string {
  return ({ None: t.dynamicNone, StartOfDay: t.dynamicStartOfDay, EndOfDay: t.dynamicEndOfDay, StartOfMonth: t.dynamicStartOfMonth, EndOfMonth: t.dynamicEndOfMonth, StartOfYear: t.dynamicStartOfYear, EndOfYear: t.dynamicEndOfYear })[boundary];
}
function formatLabel(format: DynamicValueFormat): string { return format; }
function formatsForSource(source: DynamicValueSource, types: string[]): DynamicValueFormat[] {
  if (source === "ExecutionId" || source === "IdempotencyKey") return ["String"];
  if (source === "ReferenceDate") return ["IsoDate"];
  return types.includes("integer") ? ["UnixSeconds", "UnixMilliseconds"] : ["IsoDate", "IsoOffsetTimestamp", "IsoUtcTimestamp"];
}
