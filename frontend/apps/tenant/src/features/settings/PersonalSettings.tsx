import { Alert, Chip, Paper, Stack, TextField, Typography } from "@mui/material";
import type { Labels, Language } from "@workerdock/ui";
import type { Session } from "../../auth/SessionProvider";
import { formatInstant } from "../../localTime";

interface Props {
  t: Labels;
  mode: Session["mode"];
  canManage: boolean;
  language: Language;
  setLanguage: (value: Language) => void;
  timeZone: string;
}

export function PersonalSettings({ t, mode, canManage, language, setLanguage, timeZone }: Props) {
  return (
    <Stack spacing={3}>
      <Paper component="section" aria-label={t.access} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 760 }}>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} sx={{ alignItems: { sm: "center" }, justifyContent: "space-between" }}>
          <BoxStatus t={t} mode={mode} />
          <Chip label={canManage ? t.manage : t.readOnly} size="small" color={canManage ? "primary" : "default"} variant="outlined" />
        </Stack>
        {mode === "Anonymous" && <Alert severity="warning" icon={false} sx={{ mt: 2 }}>{t.publicWarning}</Alert>}
      </Paper>
      <Paper component="section" aria-label={t.preferences} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, maxWidth: 760 }}>
        <Typography component="h2" variant="h6" sx={{ mb: 1 }}>{t.preferences}</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>{t.localPreferences}</Typography>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
          <TextField select label={t.language} value={language} onChange={(e) => setLanguage(e.target.value === "pt-BR" ? "pt-BR" : "en")} slotProps={{ select: { native: true } }} sx={{ minWidth: 180 }}>
            <option value="en">English</option><option value="pt-BR">Português (Brasil)</option>
          </TextField>
          <Typography>{t.timeZone}: {timeZone}</Typography>
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
          {t.updated}: {formatInstant(new Date(), language, timeZone)}
        </Typography>
      </Paper>
    </Stack>
  );
}

function BoxStatus({ t, mode }: Pick<Props, "t" | "mode">) {
  return <div><Typography variant="overline" color="text.secondary">{t.access}</Typography><Typography role="status" variant="body2" sx={{ fontWeight: 700 }}>{mode === "Anonymous" ? t.anonymous : mode === "RoleProtected" ? t.roleProtected : t.authenticated}</Typography></div>;
}
