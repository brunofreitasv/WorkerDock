import { MenuItem, Stack, TextField, Typography } from "@mui/material";
import { SecretReferenceField } from "./SecretReferenceField";
import { DynamicValueField } from "./DynamicValueField";
import { bindingAt, setBinding } from "./dynamicBindings";
import type { Labels } from "../preferences";
import type { DynamicValueBinding, DynamicValuePlan, DynamicValueSource } from "./dynamicValueTypes";

export interface SqlParameterEditorProps {
  value: Record<string, unknown>;
  pointer: string;
  dynamicValues: DynamicValuePlan | null;
  onChange(next: Record<string, unknown>, plan: DynamicValuePlan | null): void;
  error?: string;
  t: Labels;
  supportsDynamic: boolean;
  executionTimeZone: string;
}

const dbTypes = ["BigInt", "Int", "SmallInt", "TinyInt", "Bit", "Decimal", "Float", "Real", "Money", "SmallMoney", "NVarChar", "VarChar", "NChar", "Char", "UniqueIdentifier", "Date", "DateTime", "DateTime2", "DateTimeOffset", "Time", "Binary", "VarBinary"];
const characterTypes = new Set(["NVarChar", "VarChar", "NChar", "Char"]);
const sizedTypes = new Set([...characterTypes, "Binary", "VarBinary"]);

export function SqlParameterEditor({ value, pointer, dynamicValues, onChange, error, t, supportsDynamic, executionTimeZone }: SqlParameterEditorProps) {
  const dbType = String(value.dbType ?? "NVarChar");
  const target = `${pointer}/value`;
  const binding = bindingAt(dynamicValues, target);
  const source = binding ? "dynamic" : value.secret && typeof value.secret === "object" ? "secret" : value.isNull === true ? "null" : value.useExecutionKey === true ? "key" : "fixed";
  const dynamicSources = sourcesForType(dbType);
  const typeChanged = (next: string) => {
    const updated: Record<string, unknown> = { ...value, dbType: next };
    for (const key of ["size", "precision", "scale"]) delete updated[key];
    if (sizedTypes.has(next)) updated.size = 200;
    if (next === "Decimal") { updated.precision = 18; updated.scale = 2; }
    const nextPlan = dynamicValues && !dynamicSourcesFor(next).includes(binding?.source as DynamicValueSource) ? setBinding(dynamicValues, null, target) : dynamicValues;
    if (binding && !dynamicSourcesFor(next).includes(binding.source)) { delete updated.secret; updated.value = defaultValue(next); }
    onChange(updated, nextPlan);
  };
  const setSource = (next: string) => {
    let row: Record<string, unknown> = { ...value, useExecutionKey: false, isNull: false };
    let plan = setBinding(dynamicValues, null, target);
    delete row.secret;
    if (next === "dynamic") {
      delete row.value;
      const source = dynamicSources[0];
      if (supportsDynamic && source) plan = setBinding(plan, createBinding(target, source, dbType, executionTimeZone), target, executionTimeZone);
    } else if (next === "secret") { delete row.value; row.secret = { name: "" }; }
    else if (next === "null") { delete row.value; row.isNull = true; }
    else if (next === "key") { delete row.value; row.useExecutionKey = true; }
    else if (!Object.hasOwn(row, "value")) row.value = defaultValue(dbType);
    onChange(row, plan);
  };
  const changeBinding = (next: DynamicValueBinding | null) => {
    const row = { ...value };
    if (next) { delete row.value; row.secret = null; row.useExecutionKey = false; row.isNull = false; }
    onChange(row, setBinding(dynamicValues, next, target, executionTimeZone));
  };
  return <Stack spacing={1.5} sx={{ border: 1, borderColor: "divider", borderRadius: 1, p: 2 }}>
    <Stack direction={{ xs: "column", sm: "row" }} spacing={1}>
      <TextField label={t.sqlParameterName} value={String(value.name ?? "")} onChange={event => onChange({ ...value, name: event.target.value }, dynamicValues)} error={Boolean(error)} helperText={error} />
      <TextField select label={t.sqlDbType} value={dbType} onChange={event => typeChanged(event.target.value)}>{dbTypes.map(type => <MenuItem key={type} value={type}>{type}</MenuItem>)}</TextField>
      <TextField select label={t.dynamicValueSource} value={source} onChange={event => setSource(event.target.value)}>
        <MenuItem value="fixed">{t.sqlFixedValue}</MenuItem>
        {supportsDynamic && dynamicSources.length > 0 && <MenuItem value="dynamic">{t.sqlDynamicValue}</MenuItem>}
        <MenuItem value="secret">{t.sqlSecretValue}</MenuItem>
        <MenuItem value="null">{t.sqlNullValue}</MenuItem>
        <MenuItem value="key">{t.sqlExecutionKeyValue}</MenuItem>
      </TextField>
    </Stack>
    {source === "fixed" && <TextField label={t.sqlFixedValue} value={value.value == null ? "" : String(value.value)} type={numericType(dbType) ? "number" : "text"} onChange={event => onChange({ ...value, value: numericType(dbType) ? event.target.value === "" ? undefined : Number(event.target.value) : event.target.value }, dynamicValues)} error={Boolean(error)} helperText={error} />}
    {source === "dynamic" && supportsDynamic && <DynamicValueField binding={binding} onBindingChange={changeBinding} allowedTypes={typesForType(dbType)} allowedSources={dynamicSources} allowedFormats={formatsForType(dbType)} executionTimeZone={executionTimeZone} t={t} showMode={false} />}
    {source === "secret" && <SecretReferenceField value={String((value.secret as { name?: string } | null)?.name ?? "")} error={error} onChange={name => onChange({ ...value, value: undefined, secret: { name }, useExecutionKey: false, isNull: false }, dynamicValues)} />}
    {source === "null" && <Typography variant="caption" color="text.secondary">{t.sqlNullHelp}</Typography>}
    {source === "key" && <Typography variant="caption" color="text.secondary">{t.sqlExecutionKeyHelp}</Typography>}
    {sizedTypes.has(dbType) && <TextField type="number" label={t.sqlParameterSize} value={value.size ?? ""} onChange={event => onChange({ ...value, size: event.target.value === "" ? undefined : Number(event.target.value) }, dynamicValues)} helperText={t.sqlSizeHelp} slotProps={{ htmlInput: { min: -1, max: 1048576, step: 1 } }} />}
    {dbType === "Decimal" && <>
      <Stack direction="row" spacing={1}>
        <TextField type="number" label={t.sqlParameterPrecision} value={value.precision ?? ""} onChange={event => onChange({ ...value, precision: event.target.value === "" ? undefined : Number(event.target.value) }, dynamicValues)} slotProps={{ htmlInput: { min: 1, max: 38, step: 1 } }} />
        <TextField type="number" label={t.sqlParameterScale} value={value.scale ?? ""} onChange={event => onChange({ ...value, scale: event.target.value === "" ? undefined : Number(event.target.value) }, dynamicValues)} slotProps={{ htmlInput: { min: 0, max: 38, step: 1 } }} />
      </Stack>
      <Typography variant="caption" color="text.secondary">{t.sqlDecimalHelp}</Typography>
    </>}
  </Stack>;
}

function sourcesForType(type: string): DynamicValueSource[] { return dynamicSourcesFor(type); }
function dynamicSourcesFor(type: string): DynamicValueSource[] {
  if (characterTypes.has(type)) return ["ReferenceDate", "ScheduledTimestamp", "FirstExecutionStart", "ExecutionId", "IdempotencyKey"];
  if (type === "Date" || type === "DateTime" || type === "DateTime2") return ["ReferenceDate", "ScheduledTimestamp", "FirstExecutionStart"];
  if (type === "DateTimeOffset") return ["ScheduledTimestamp", "FirstExecutionStart"];
  if (type === "BigInt") return ["ScheduledTimestamp", "FirstExecutionStart"];
  if (type === "UniqueIdentifier") return ["ExecutionId"];
  return [];
}
function typesForType(type: string): string[] { return type === "BigInt" ? ["integer"] : ["string"]; }
function formatsForType(type: string): import("./dynamicValueTypes").DynamicValueFormat[] {
  if (type === "BigInt") return ["UnixSeconds", "UnixMilliseconds"];
  if (type === "Date") return ["IsoDate"];
  if (type === "DateTime" || type === "DateTime2") return ["IsoDate", "IsoOffsetTimestamp", "IsoUtcTimestamp"];
  if (type === "DateTimeOffset") return ["IsoOffsetTimestamp", "IsoUtcTimestamp"];
  if (type === "UniqueIdentifier") return ["String"];
  return ["String", "IsoDate", "IsoOffsetTimestamp", "IsoUtcTimestamp"];
}
function createBinding(target: string, source: DynamicValueSource, type: string, zone: string): DynamicValueBinding {
  const isIdentifier = source === "ExecutionId" || source === "IdempotencyKey";
  const isInteger = type === "BigInt";
  const format = isIdentifier ? "String" : isInteger ? "UnixSeconds" : type === "DateTimeOffset" ? "IsoOffsetTimestamp" : "IsoDate";
  return { target, source, timeZoneId: source === "ReferenceDate" || isIdentifier ? null : zone, months: 0, days: 0, boundary: "None", format };
}
function defaultValue(type: string): unknown { return type === "Bit" ? false : numericType(type) ? 0 : ""; }
function numericType(type: string): boolean { return ["BigInt", "Int", "SmallInt", "TinyInt", "Decimal", "Float", "Real", "Money", "SmallMoney"].includes(type); }
