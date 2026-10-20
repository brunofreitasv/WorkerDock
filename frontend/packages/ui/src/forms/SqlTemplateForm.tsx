import { Alert, Button, MenuItem, Stack, TextField, Typography } from "@mui/material";
import type { Labels } from "../preferences";
import { translations } from "../preferences";
import type { TemplateFormProps } from "./TemplateForm";
import { SqlParameterEditor } from "./SqlParameterEditor";
import { removeArrayItem } from "./dynamicBindings";
import type { DynamicValuePlan } from "./dynamicValueTypes";

export function SqlTemplateForm(props: TemplateFormProps) {
  const { descriptor, value, onChange, errors } = props;
  const t: Labels = props.t ?? translations.en;
  const dynamicValues = props.dynamicValues ?? null;
  const supportsDynamic = descriptor.version === "1.1.0" && props.onDynamicValuesChange !== undefined;
  const parameters = Array.isArray(value.parameters) ? value.parameters as Record<string, unknown>[] : [];
  const kind = value.commandKind === "Procedure" ? "Procedure" : "Query";
  const set = (key: string, next: unknown) => onChange({ ...value, [key]: next });
  const updatePlan = (next: DynamicValuePlan | null) => props.onDynamicValuesChange?.(next);
  const updateRow = (index: number, row: Record<string, unknown>, plan: DynamicValuePlan | null) => {
    onChange({ ...value, parameters: parameters.map((item, i) => i === index ? row : item) });
    updatePlan(plan);
  };
  return <Stack component="fieldset" spacing={2} sx={{ border: 0, p: 0, m: 0, minWidth: 0 }}>
    <Typography component="legend" variant="h6">{descriptor.displayName}</Typography>
    <Typography color="text.secondary">{descriptor.description}</Typography>
    {errors.configuration && <Typography color="error" role="alert">Configuration: {errors.configuration}</Typography>}
    <TextField label={t.sqlProfileId} value={String(value.profileId ?? "")} onChange={event => set("profileId", event.target.value)} error={Boolean(errors["configuration.profileId"])} helperText={errors["configuration.profileId"]} />
    <TextField label={t.sqlCatalog} value={String(value.catalog ?? "")} onChange={event => set("catalog", event.target.value)} error={Boolean(errors["configuration.catalog"])} helperText={errors["configuration.catalog"]} />
    <TextField select label={t.sqlCommandKind} value={kind} onChange={event => set("commandKind", event.target.value)}>
      <MenuItem value="Query">{t.sqlQuery}</MenuItem><MenuItem value="Procedure">{t.sqlProcedure}</MenuItem>
    </TextField>
    <TextField fullWidth multiline minRows={4} label={kind === "Procedure" ? t.sqlStoredProcedureName : t.sqlStatement}
      value={String(value.commandText ?? "")} onChange={event => set("commandText", event.target.value)}
      error={Boolean(errors["configuration.commandText"])}
      helperText={errors["configuration.commandText"] || (kind === "Procedure" ? `${t.sqlProcedureHelp} ${t.sqlExecExample}` : `${t.sqlQueryHelp} ${t.sqlSelectExample}`)} />
    <TextField type="number" label={t.sqlTimeoutSeconds} value={value.commandTimeoutSeconds ?? ""}
      onChange={event => set("commandTimeoutSeconds", event.target.value === "" ? null : Number(event.target.value))}
      error={Boolean(errors["configuration.commandTimeoutSeconds"])} helperText={errors["configuration.commandTimeoutSeconds"] || undefined}
      slotProps={{ htmlInput: { min: 0, step: 1 } }} />
    {value.commandTimeoutSeconds == null || value.commandTimeoutSeconds === ""
      ? <Alert severity="warning">{t.sqlTimeoutWarning}{props.jobTimeout ? ` ${t.sqlTimeoutLimitedByJob}` : ""}</Alert>
      : null}
    <TextField select label={t.sqlUseTransaction} value={String(value.useTransaction ?? true)} onChange={event => set("useTransaction", event.target.value === "true")}>
      <MenuItem value="true">true</MenuItem><MenuItem value="false">false</MenuItem>
    </TextField>
    <Typography variant="subtitle1">{t.sqlParameters}</Typography>
    {!supportsDynamic && descriptor.version === "1.0.0" && <Alert severity="info">{t.sqlLegacyDynamicHelp}</Alert>}
    {parameters.map((parameter, index) => <Stack key={index} spacing={1}>
      <SqlParameterEditor value={parameter} pointer={`/parameters/${index}`} dynamicValues={dynamicValues}
        onChange={(row, plan) => updateRow(index, row, plan)} error={errors[`configuration.parameters[${index}].value`] || errors[`configuration.parameters[${index}].name`]}
        t={t} supportsDynamic={supportsDynamic} executionTimeZone={dynamicValues?.timeZoneId ?? "UTC"} />
      <Button color="inherit" onClick={() => { set("parameters", parameters.filter((_, row) => row !== index)); updatePlan(removeArrayItem(dynamicValues, "/parameters", index)); }}>{t.sqlRemoveParameter}</Button>
    </Stack>)}
    <Button disabled={parameters.length >= 128} onClick={() => {
      const next = { name: `@Parameter${parameters.length + 1}`, dbType: "NVarChar", value: "", useExecutionKey: false, isNull: false, size: 200 };
      set("parameters", [...parameters, next]);
    }}>{t.sqlAddParameter}</Button>
  </Stack>;
}
