import { useEffect, useState, type FormEvent } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Divider, Stack, TextField, Typography } from "@mui/material";

export type TenantRegistrationInput = {
  displayName: string;
  expectedInstanceId: string;
  dashboardUrl: string;
  healthCredentialReference: string;
};

type Props = {
  locale?: "en" | "pt-BR";
  open: boolean;
  initialValue?: TenantRegistrationInput;
  busy?: boolean;
  onClose: () => void;
  onSave: (input: TenantRegistrationInput) => Promise<void>;
};

const empty: TenantRegistrationInput = { displayName: "", expectedInstanceId: "", dashboardUrl: "", healthCredentialReference: "" };

const editorCopy = {
  en: { editTitle: "Edit tenant link", addTitle: "Register tenant", identity: "Tenant identity", name: "Tenant name", instanceId: "Expected instance ID", instanceHelp: "Used to verify the health response belongs to this tenant.", connection: "Connection", dashboardUrl: "Dashboard URL", dashboardHelp: "HTTPS links only. This creates a link; it does not deploy or reconfigure the tenant.", credential: "Health credential reference", credentialHelp: "Enter a server-managed vault reference such as TENANT_HEALTH_TOKEN. Never paste a token or password here.", cancel: "Cancel", save: "Save tenant", saving: "Saving…", httpsError: "Use an HTTPS dashboard URL.", nameError: "Enter a tenant name.", idError: "Enter a valid instance ID.", credentialError: "Enter a valid server-managed credential reference.", saveError: "Could not save registration." },
  "pt-BR": { editTitle: "Editar link do tenant", addTitle: "Cadastrar tenant", identity: "Identidade do tenant", name: "Nome do tenant", instanceId: "ID esperado da instância", instanceHelp: "Usado para verificar se a resposta de saúde pertence a este tenant.", connection: "Conexão", dashboardUrl: "URL do painel", dashboardHelp: "Apenas links HTTPS. Isso cria um link; não implanta nem reconfigura o tenant.", credential: "Referência da credencial de saúde", credentialHelp: "Informe uma referência de cofre gerenciada pelo servidor, como TENANT_HEALTH_TOKEN. Nunca cole um token ou senha aqui.", cancel: "Cancelar", save: "Salvar tenant", saving: "Salvando…", httpsError: "Use uma URL HTTPS para o painel.", nameError: "Informe o nome do tenant.", idError: "Informe um ID de instância válido.", credentialError: "Informe uma referência válida de credencial gerenciada pelo servidor.", saveError: "Não foi possível salvar o cadastro." },
};

export default function RegistrationEditor({ locale = "en", open, initialValue, busy = false, onClose, onSave }: Props) {
  const t = editorCopy[locale];
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
      if (url.protocol !== "https:") throw new Error(t.httpsError);
      if (!value.displayName.trim()) throw new Error(t.nameError);
      if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value.expectedInstanceId.trim())) {
        throw new Error(t.idError);
      }
      if (!/^[A-Z0-9_]{1,128}$/.test(value.healthCredentialReference.trim())) {
        throw new Error(t.credentialError);
      }
      await onSave({
        displayName: value.displayName.trim(),
        expectedInstanceId: value.expectedInstanceId.trim(),
        dashboardUrl: url.toString(),
        healthCredentialReference: value.healthCredentialReference.trim(),
      });
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t.saveError);
    }
  }

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm" aria-labelledby="registration-title">
      <form onSubmit={submit}>
        <DialogTitle id="registration-title">{initialValue ? t.editTitle : t.addTitle}</DialogTitle>
        <DialogContent>
          <Stack spacing={2.25} sx={{ pt: 1 }}>
            {error && <Alert severity="error" role="alert">{error}</Alert>}
            <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{t.identity}</Typography>
            <TextField autoFocus required fullWidth label={t.name} value={value.displayName}
              onChange={event => setValue(current => ({ ...current, displayName: event.target.value }))} />
            <TextField required fullWidth label={t.instanceId} value={value.expectedInstanceId}
              helperText={t.instanceHelp}
              onChange={event => setValue(current => ({ ...current, expectedInstanceId: event.target.value }))} />
            <Divider />
            <Typography component="h3" variant="subtitle1" sx={{ fontWeight: 700 }}>{t.connection}</Typography>
            <TextField required fullWidth label={t.dashboardUrl} type="url" value={value.dashboardUrl}
              helperText={t.dashboardHelp}
              onChange={event => setValue(current => ({ ...current, dashboardUrl: event.target.value }))} />
            <TextField required fullWidth label={t.credential} value={value.healthCredentialReference}
              helperText={t.credentialHelp}
              onChange={event => setValue(current => ({ ...current, healthCredentialReference: event.target.value.toUpperCase() }))} />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2.5 }}>
          <Button onClick={onClose} disabled={busy}>{t.cancel}</Button>
          <Button type="submit" variant="contained" disabled={busy}>{busy ? t.saving : t.save}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
