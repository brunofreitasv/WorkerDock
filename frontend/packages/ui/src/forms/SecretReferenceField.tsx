import { TextField } from "@mui/material";

export function SecretReferenceField({ value, onChange, error, label = "Secret reference" }: { value: string; onChange(value: string): void; error?: string; label?: string }) {
  return <TextField fullWidth label={label} value={value} onChange={(event) => onChange(event.target.value)} error={Boolean(error)} helperText={error || "Use a logical secret reference. Secret values are entered in the secret provider."} autoComplete="off" slotProps={{ htmlInput: { maxLength: 128, autoCapitalize: "none", spellCheck: false } }} />;
}
