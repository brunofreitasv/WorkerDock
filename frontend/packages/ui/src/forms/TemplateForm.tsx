import { Box, Button, MenuItem, Stack, TextField, Typography } from "@mui/material";
import { Fragment } from "react";
import { SecretReferenceField } from "./SecretReferenceField";
import { createSchemaDefault, isSecretReferenceSchema } from "./schemaDefaults";
import { translations } from "../preferences";
import type { Labels } from "../preferences";
import { DynamicValueField } from "./DynamicValueField";
import { bindingAt, encodePointerSegment, removeArrayItem, removeMapEntry, renameMapEntry, setBinding } from "./dynamicBindings";
import type { DynamicValuePlan } from "./dynamicValueTypes";
import { SqlTemplateForm } from "./SqlTemplateForm";

export interface TemplateDescriptorDto {
  id: string; version: string; displayName: string; description: string;
  configurationSchema: Record<string, any>;
  uiHints?: { fields?: Record<string, { control?: string; labelKey?: string; descriptionKey?: string; placeholderKey?: string }> };
}
export interface TemplateFormProps {
  descriptor: TemplateDescriptorDto;
  value: Record<string, unknown>;
  onChange(value: Record<string, unknown>): void;
  errors: Record<string, string>;
  dynamicValues?: DynamicValuePlan | null;
  onDynamicValuesChange?(next: DynamicValuePlan | null): void;
  t?: Labels;
  jobTimeout?: string | null;
}

export function TemplateForm(props: TemplateFormProps) {
  if (props.descriptor.id === "workerdock.sql-server") return <SqlTemplateForm {...props} />;
  const { descriptor, value, onChange, errors, dynamicValues, onDynamicValuesChange } = props;
  const t = props.t ?? translations.en;
  return <Box component="fieldset" sx={{ border: 0, p: 0, m: 0, minWidth: 0 }}>
    <Typography component="legend" variant="h6">{descriptor.displayName}</Typography>
    <Typography color="text.secondary" sx={{ mb: 2 }}>{descriptor.description}</Typography>
    {errors.configuration && <Typography color="error" role="alert" sx={{ mb: 2 }}>Configuration: {errors.configuration}</Typography>}
    <Node schema={descriptor.configurationSchema} value={value} path="configuration" pointer="" onChange={(next) => onChange(next as Record<string, unknown>)} errors={errors} descriptor={descriptor} dynamicValues={dynamicValues} onDynamicValuesChange={onDynamicValuesChange} t={t} />
  </Box>;
}

function Node({ schema, value, path, pointer, onChange, errors, descriptor, dynamicValues, onDynamicValuesChange, t }: { schema: any; value: any; path: string; pointer: string; onChange(value: any): void; errors: Record<string, string>; descriptor: TemplateDescriptorDto; dynamicValues?: DynamicValuePlan | null; onDynamicValuesChange?: (value: DynamicValuePlan | null) => void; t: Labels }) {
  if (!schema || schema === false) return null;
  if (schema.$ref?.startsWith("#/$defs/")) schema = descriptor.configurationSchema.$defs?.[schema.$ref.slice(8)] ?? schema;
  if (schema.oneOf) {
    const branches = schema.oneOf as any[];
    const tag = discriminator(branches);
    const current = tag ? value?.[tag] : undefined;
    const selected = branches.find((branch) => tag && branch.properties?.[tag]?.const === current) ?? branches[0];
    return <Stack spacing={2} sx={{ my: 2 }}>
      {tag && <TextField select label={labelFor(path, descriptor)} value={current ?? selected?.properties?.[tag]?.const ?? ""} onChange={(event) => {
        const branch = branches.find((candidate) => candidate.properties?.[tag]?.const === event.target.value);
        const next: Record<string, unknown> = { ...(value ?? {}), [tag]: event.target.value };
        const branchDefaults = branch ? createSchemaDefault(branch, descriptor.configurationSchema) as Record<string, unknown> : {};
        for (const key of Object.keys(value ?? {})) if (!branch?.properties?.[key]) delete next[key];
        Object.assign(next, branchDefaults);
        onChange(next);
      }}>{branches.map((branch, index) => <MenuItem key={index} value={branch.properties?.[tag]?.const ?? index}>{String(branch.properties?.[tag]?.const ?? index)}</MenuItem>)}</TextField>}
      <Node schema={selected} value={value} path={path} pointer={pointer} onChange={onChange} errors={errors} descriptor={descriptor} dynamicValues={dynamicValues} onDynamicValuesChange={onDynamicValuesChange} t={t} />
      {path !== "configuration" && errors[path] && <Typography color="error" role="alert">{errors[path]}</Typography>}
    </Stack>;
  }
  if (schema["x-workerDock-secretReference"] === true) return <SecretReferenceField value={value ?? ""} error={errors[path]} onChange={onChange} />;
  if (schema.type === "object" || schema.properties || (value && typeof value === "object" && !Array.isArray(value))) {
    const object = (value && typeof value === "object" ? value : {}) as Record<string, any>;
    const properties = schema.properties ?? {};
    return <Stack spacing={2} sx={{ my: 2 }}>
      {Object.entries(properties).map(([key, child]: [string, any]) => <Fragment key={key}>
        <Node schema={child} value={object[key]} path={`${path}.${key}`} pointer={`${pointer}/${encodePointerSegment(key)}`} onChange={(next) => onChange(updateProperty(object, key, next))} errors={errors} descriptor={descriptor} dynamicValues={dynamicValues} onDynamicValuesChange={onDynamicValuesChange} t={t} />
      </Fragment>)}
      {!schema.properties && schema.additionalProperties && Object.entries(object).map(([key, entry]) => <Stack key={key} direction={{ xs: "column", sm: "row" }} spacing={1}>
        <TextField label={t.sqlParameterName} value={key} onChange={(event) => { const renamed = event.target.value; if (unsafeMapKey(renamed) || (renamed !== key && Object.hasOwn(object, renamed))) return; const next = { ...object }; delete next[key]; onChange({ ...next, [renamed]: entry }); onDynamicValuesChange?.(renameMapEntry(dynamicValues ?? null, pointer, key, renamed)); }} />
        <Node schema={schema.additionalProperties} value={entry} path={`${path}.${key}`} pointer={`${pointer}/${encodePointerSegment(key)}`} onChange={(next) => onChange(updateProperty(object, key, next))} errors={errors} descriptor={descriptor} dynamicValues={dynamicValues} onDynamicValuesChange={onDynamicValuesChange} t={t} />
        <Button onClick={() => { const next = { ...object }; delete next[key]; onChange(next); onDynamicValuesChange?.(removeMapEntry(dynamicValues ?? null, pointer, key)); }}>{t.genericRemoveEntry}</Button>
      </Stack>)}
      {!schema.properties && schema.additionalProperties && <Button disabled={isSecretReferenceSchema(schema.additionalProperties, descriptor.configurationSchema)} onClick={() => onChange({ ...object, [`value${Object.keys(object).length + 1}`]: createSchemaDefault(schema.additionalProperties, descriptor.configurationSchema) ?? "" })}>{t.genericAddEntry}</Button>}
    </Stack>;
  }
  if (schema.type === "array" || Array.isArray(value)) {
    const items = Array.isArray(value) ? value : [];
    return <Stack spacing={2} sx={{ my: 2 }}>
      <Typography variant="subtitle2">{labelFor(path, descriptor)}</Typography>
      {items.map((item, index) => <Stack key={index} direction={{ xs: "column", sm: "row" }} spacing={1}>
        <Node schema={schema.items ?? { type: "string" }} value={item} path={`${path}[${index}]`} pointer={`${pointer}/${index}`} onChange={(next) => onChange(items.map((entry, i) => i === index ? next : entry))} errors={errors} descriptor={descriptor} dynamicValues={dynamicValues} onDynamicValuesChange={onDynamicValuesChange} t={t} />
        <Button onClick={() => { onChange(items.filter((_, i) => i !== index)); onDynamicValuesChange?.(removeArrayItem(dynamicValues ?? null, pointer, index)); }}>{t.genericRemoveItem}</Button>
      </Stack>)}
      <Button disabled={isSecretReferenceSchema(schema.items ?? { type: "string" }, descriptor.configurationSchema)} onClick={() => onChange([...items, createSchemaDefault(schema.items ?? { type: "string" }, descriptor.configurationSchema) ?? ""])}>{t.genericAddItem}</Button>
      {path !== "configuration" && errors[path] && <Typography color="error" role="alert">{errors[path]}</Typography>}
    </Stack>;
  }
  const types = Array.isArray(schema.type) ? schema.type.filter((type: string) => type !== "null") : [schema.type ?? typeof value];
  const effectiveType = types.includes("integer") || types.includes("number") ? (types.includes("integer") ? "integer" : "number") : types[0];
  const options = schema.enum as unknown[] | undefined;
  const choiceOptions = effectiveType === "boolean" ? ["false", "true"] : options?.map(String);
  const field = <TextField fullWidth select={Boolean(choiceOptions)} type={effectiveType === "integer" || effectiveType === "number" ? "number" : "text"} multiline={path.endsWith(".commandText") || path.endsWith(".body")} minRows={path.endsWith(".commandText") || path.endsWith(".body") ? 4 : undefined} label={labelFor(path, descriptor)} value={effectiveType === "boolean" ? String(value ?? false) : value ?? ""} onChange={(event) => {
    const raw = event.target.value;
    onChange(effectiveType === "integer" ? raw === "" ? undefined : Number.parseInt(raw, 10) : effectiveType === "number" ? raw === "" ? undefined : Number(raw) : effectiveType === "boolean" ? raw === "true" : raw);
  }} error={Boolean(errors[path])} helperText={errors[path] || schema.description || undefined} slotProps={{ htmlInput: { min: schema.minimum, max: schema.maximum, step: effectiveType === "integer" ? 1 : "any", maxLength: schema.maxLength } }}>
    {choiceOptions?.map((option) => <MenuItem key={option} value={option}>{option}</MenuItem>)}
  </TextField>;
  if (schema["x-workerDock-dynamicValue"] !== true || !onDynamicValuesChange || !(effectiveType === "string" || effectiveType === "integer" || effectiveType === "number")) return field;
  const target = pointer || "/";
  const binding = bindingAt(dynamicValues, target);
  const allowedTypes = types.filter((type: string) => type !== "null");
  return <Stack spacing={1}><DynamicValueField binding={binding} allowedTypes={allowedTypes} executionTimeZone={dynamicValues?.timeZoneId ?? "UTC"} t={t} onBindingChange={next => { onDynamicValuesChange(setBinding(dynamicValues, next, target, dynamicValues?.timeZoneId ?? "UTC")); if (next) onChange(undefined); }} />{!binding && field}</Stack>;
}

function updateProperty(object: Record<string, any>, key: string, value: unknown): Record<string, any> { const next = { ...object }; if (value === undefined) delete next[key]; else next[key] = value; return next; }
function unsafeMapKey(value: string): boolean { return value === "__proto__" || value === "prototype" || value === "constructor"; }

function discriminator(branches: any[]): string | undefined {
  const keys = Object.keys(branches[0]?.properties ?? {});
  return keys.find((key) => branches.every((branch) => branch.required?.includes(key) && typeof branch.properties?.[key]?.const === "string"));
}
function labelFor(path: string, descriptor: TemplateDescriptorDto): string {
  const key = path.slice(path.lastIndexOf(".") + 1);
  const explicit = descriptor.uiHints?.fields?.[key]?.labelKey;
  const known: Record<string, string> = { url: "URL", profileId: "Profile ID", commandKind: "Command kind", useTransaction: "Use transaction", maxResponseBytes: "Maximum response bytes", responseMode: "Response mode", successStatuses: "Success statuses", secretHeaders: "Secret headers", idempotencyHeader: "Idempotency header", commandText: "Command text", commandTimeoutSeconds: "Command timeout seconds" };
  if (known[key]) return known[key];
  const label = explicit ?? key;
  return label.replace(/([A-Z])/g, " $1").replace(/^./, (first) => first.toUpperCase());
}
