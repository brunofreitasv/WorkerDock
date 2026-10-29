import { Alert, Chip, Paper, Stack, TextField, Typography } from "@mui/material";
import { timeZones, type Labels, type Language } from "@workerdock/ui";
import type { Session } from "../../auth/SessionProvider";

interface Props {
  t: Labels;
  mode: Session["mode"];
  canManage: boolean;
  language: Language;
  setLanguage: (value: Language) => void;
  timeZone: string;
  setTimeZone: (value: string) => void;
}

export function PersonalSettings({ t, mode, canManage, language, setLanguage, timeZone, setTimeZone }: Props) {
  return (
    <Stack spacing={3}>
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 760 }}>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between" }}>
          <BoxStatus t={t} mode={mode} />
          <Chip label={canManage ? t.manage : t.readOnly} size="small" color={canManage ? "primary" : "default"} variant="outlined" />
        </Stack>
        {mode === "Anonymous" && <Alert severity="warning" icon={false} sx={{ mt: 2 }}>{t.publicWarning}</Alert>}
      </Paper>
      <Paper variant="outlined" sx={{ p: 3, maxWidth: 760 }}>
        <Typography component="h2" variant="h6" sx={{ mb: 1 }}>{t.preferences}</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>{t.localPreferences}</Typography>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
          <TextField select label={t.language} value={language} onChange={(e) => setLanguage(e.target.value === "pt-BR" ? "pt-BR" : "en")} slotProps={{ select: { native: true } }} sx={{ minWidth: 180 }}>
            <option value="en">English</option><option value="pt-BR">Português (Brasil)</option>
          </TextField>
          <TextField select label={t.timeZone} value={timeZone} onChange={(e) => setTimeZone(e.target.value)} slotProps={{ select: { native: true } }} sx={{ minWidth: 200 }}>
            {timeZones.map((zone) => <option key={zone} value={zone}>{zone}</option>)}
          </TextField>
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
          {t.updated}: {new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date())}
        </Typography>
      </Paper>
    </Stack>
  );
}

function BoxStatus({ t, mode }: Pick<Props, "t" | "mode">) {
  return <div><Typography variant="overline" color="text.secondary">{t.access}</Typography><Typography role="status" variant="body2" sx={{ fontWeight: 700 }}>{mode === "Anonymous" ? t.anonymous : mode === "RoleProtected" ? t.roleProtected : t.authenticated}</Typography></div>;
}
