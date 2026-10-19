import { useEffect, useState, type FormEvent } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField } from "@mui/material";

export type TenantRegistrationInput = {
  displayName: string;
  expectedInstanceId: string;
  dashboardUrl: string;
  healthCredentialReference: string;
};

type Props = {
  open: boolean;
  initialValue?: TenantRegistrationInput;
  busy?: boolean;
  onClose: () => void;
  onSave: (input: TenantRegistrationInput) => Promise<void>;
};

const empty: TenantRegistrationInput = { displayName: "", expectedInstanceId: "", dashboardUrl: "", healthCredentialReference: "" };

export default function RegistrationEditor({ open, initialValue, busy = false, onClose, onSave }: Props) {
  const [value, setValue] = useState<TenantRegistrationInput>(initialValue ?? empty);
  const [error, setError] = useState("");

  useEffect(() => {
    if (open) {
      setValue(initialValue ?? empty);
      setError("");
    }
  }, [open, initialValue]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    try {
      const url = new URL(value.dashboardUrl);
      if (url.protocol !== "https:") throw new Error("Use an HTTPS dashboard URL.");
      if (!value.displayName.trim()) throw new Error("Enter a tenant name.");
      if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value.expectedInstanceId.trim())) {
        throw new Error("Enter a valid instance ID.");
      }
      if (!/^[A-Z0-9_]{1,128}$/.test(value.healthCredentialReference.trim())) {
        throw new Error("Enter a valid server-managed credential reference.");
      }
      await onSave({
        displayName: value.displayName.trim(),
        expectedInstanceId: value.expectedInstanceId.trim(),
        dashboardUrl: url.toString(),
        healthCredentialReference: value.healthCredentialReference.trim(),
      });
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Could not save registration.");
    }
  }

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" aria-labelledby="registration-title">
      <form onSubmit={submit}>
        <DialogTitle id="registration-title">{initialValue ? "Edit tenant link" : "Register tenant"}</DialogTitle>
        <DialogContent>
          <Stack spacing={2.25} sx={{ pt: 1 }}>
            {error && <Alert severity="error" role="alert">{error}</Alert>}
            <TextField autoFocus required fullWidth label="Tenant name" value={value.displayName}
              onChange={event => setValue(current => ({ ...current, displayName: event.target.value }))} />
            <TextField required fullWidth label="Expected instance ID" value={value.expectedInstanceId}
              helperText="Used to verify the health response belongs to this tenant."
              onChange={event => setValue(current => ({ ...current, expectedInstanceId: event.target.value }))} />
            <TextField required fullWidth label="Dashboard URL" type="url" value={value.dashboardUrl}
              helperText="HTTPS links only. This creates a link; it does not deploy or reconfigure the tenant."
              onChange={event => setValue(current => ({ ...current, dashboardUrl: event.target.value }))} />
            <TextField required fullWidth label="Health credential reference" value={value.healthCredentialReference}
              helperText="Enter a server-managed vault reference such as TENANT_HEALTH_TOKEN. Never paste a token or password here."
              onChange={event => setValue(current => ({ ...current, healthCredentialReference: event.target.value.toUpperCase() }))} />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2.5 }}>
          <Button onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={busy}>{busy ? "Saving…" : "Save tenant"}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
