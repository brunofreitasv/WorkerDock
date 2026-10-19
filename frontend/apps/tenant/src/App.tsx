import { useEffect, useState, type FormEvent } from "react";
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  CssBaseline,
  Paper,
  Stack,
  TextField,
  ThemeProvider,
  Typography,
  createTheme,
} from "@mui/material";
import {
  AppShell,
  usePreferences,
  timeZones,
  type Labels,
} from "@workerdock/ui";
import { useSession } from "./auth/SessionProvider";
import { ApiError, mutate, read, signOut } from "./api/client";
import { VaultSettingsPage } from "./features/settings/VaultSettingsPage";
import { OutboundPolicyPage } from "./features/settings/OutboundPolicyPage";
import { ManualJobEditor } from "./features/jobs/ManualJobEditor";
import { ProfileEditor } from "./features/profiles/ProfileEditor";
import { TemplatePicker, type CatalogItem } from "./features/templates/TemplatePicker";
const theme = createTheme({
  palette: {
    primary: { main: "#0b6c61", dark: "#064f47", light: "#dff2ed" },
    background: { default: "#f5f7f8", paper: "#fff" },
    text: { primary: "#182d39", secondary: "#536772" },
  },
  typography: {
    fontFamily: "Roboto, sans-serif",
    h4: { fontWeight: 700, letterSpacing: "-.035em" },
    button: { textTransform: "none", fontWeight: 600 },
  },
  shape: { borderRadius: 10 },
});
interface Settings {
  displayName: string;
  displayTimeZone: string;
  version: string;
}
function SettingsForm({ t }: { t: Labels }) {
  const [settings, setSettings] = useState<Settings>();
  const [etag, setEtag] = useState<string>();
  const [name, setName] = useState("");
  const [zone, setZone] = useState("UTC");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  const [invalid, setInvalid] = useState(false);
  const load = () => {
    setError("");
    void read<Settings>("/api/settings")
      .then((result) => {
        setSettings(result.data);
        setEtag(result.etag ?? undefined);
        setName(result.data.displayName);
        setZone(result.data.displayTimeZone);
      })
      .catch(() => setError(t.requestError));
  };
  useEffect(load, [t]);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    setInvalid(true);
    if (!name.trim() || name.length > 200 || !timeZones.includes(zone)) return;
    setBusy(true);
    setError("");
    try {
      const updated = await mutate<Settings>(
        "/api/settings",
        "PUT",
        { displayName: name, displayTimeZone: zone },
        etag,
      );
      setSettings(updated);
      setEtag('"' + updated.version + '"');
      setSaved(true);
      setInvalid(false);
    } catch (error) {
      setError(
        error instanceof ApiError && error.status === 412
          ? t.stale
          : t.requestError,
      );
    } finally {
      setBusy(false);
    }
  };
  return (
    <Paper variant="outlined" sx={{ p: 3, maxWidth: 620 }}>
      {error && (
        <Alert
          severity="error"
          action={
            <Button color="inherit" onClick={load}>
              {t.reload}
            </Button>
          }
          sx={{ mb: 2 }}
        >
          {error}
        </Alert>
      )}
      {saved && (
        <Alert severity="success" sx={{ mb: 2 }}>
          {t.saved}
        </Alert>
      )}
      {!settings && !error ? (
        <CircularProgress aria-label={t.loading} />
      ) : (
        <Box component="form" onSubmit={submit} noValidate>
          <Stack spacing={3}>
            <TextField
              label={t.displayName}
              value={name}
              onChange={(e) => setName(e.target.value)}
              error={invalid && (!name.trim() || name.length > 200)}
              helperText={
                invalid && (!name.trim() || name.length > 200)
                  ? t.requiredName
                  : undefined
              }
              required
              fullWidth
            />
            <TextField
              label={t.displayTimeZone}
              value={zone}
              onChange={(e) => setZone(e.target.value)}
              error={invalid && !timeZones.includes(zone)}
              helperText={
                invalid && !timeZones.includes(zone) ? t.invalidZone : undefined
              }
              select
              slotProps={{ select: { native: true } }}
            >
              {timeZones.map((value) => (
                <option key={value} value={value}>
                  {value}
                </option>
              ))}
            </TextField>
            <Button
              type="submit"
              variant="contained"
              disabled={busy || !settings || !etag}
              sx={{ alignSelf: "flex-start" }}
            >
              {busy ? t.loading : t.save}
            </Button>
          </Stack>
        </Box>
      )}
    </Paper>
  );
}
export default function App() {
  const { session, loading, error, retry } = useSession();
  const preferences = usePreferences(session?.timeZone);
  const { t } = preferences;
  const [active, setActive] = useState(
    () => window.location.hash.slice(2) || "dashboard",
  );
  const [settingsTab, setSettingsTab] = useState("general");
  const [catalogItem, setCatalogItem] = useState<CatalogItem>();
  const [actionError, setActionError] = useState(false);
  useEffect(() => {
    const change = () =>
      setActive(window.location.hash.slice(2) || "dashboard");
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);
  const navigate = (id: string) => {
    setActive(id);
    window.history.replaceState(null, "", "#/" + id);
  };
  const navigation = session?.canRead
    ? [
        { id: "dashboard", label: t.dashboard },
        { id: "monitoring", label: t.monitoring },
        { id: "audit", label: t.audit },
        { id: "templates", label: t.templates },
        ...(session.canManage ? [{ id: "jobs", label: t.jobs }] : []),
        ...(session.canManage ? [{ id: "settings", label: t.settings }] : []),
      ]
    : [];
  const title =
    navigation.find((item) => item.id === active)?.label ??
    (active === "settings" ? t.settings : t.dashboard);
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      {loading ? (
        <Box sx={{ p: 5 }}>
          <CircularProgress aria-label={t.loading} />
        </Box>
      ) : error || !session ? (
        <Box sx={{ p: 5 }}>
          <Alert
            severity="error"
            action={
              <Button color="inherit" onClick={retry}>
                {t.retry}
              </Button>
            }
          >
            {t.sessionError}
          </Alert>
        </Box>
      ) : (
        <AppShell
          t={t}
          navigation={navigation}
          active={active}
          onNavigate={navigate}
          mode={session.mode}
          canManage={session.canManage}
          authenticated={session.authenticated}
          onSignOut={() => {
            void signOut().catch(() => setActionError(true));
          }}
        >
          {actionError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {t.requestError}
            </Alert>
          )}
          <Typography component="h1" variant="h4" sx={{ mb: 1 }}>
            {title}
          </Typography>
          {session.canRead ? (
            <>
              {active === "settings" ? (
                session.canManage ? (
                  <>
                    <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mb: 3 }}>
                      <Button variant={settingsTab === "general" ? "contained" : "outlined"} onClick={() => setSettingsTab("general")}>{t.settings}</Button>
                      <Button variant={settingsTab === "vault" ? "contained" : "outlined"} onClick={() => setSettingsTab("vault")}>{t.vaultSettings}</Button>
                      <Button variant={settingsTab === "outbound" ? "contained" : "outlined"} onClick={() => setSettingsTab("outbound")}>{t.outboundSettings}</Button>
                      <Button variant={settingsTab === "profiles" ? "contained" : "outlined"} onClick={() => setSettingsTab("profiles")}>{t.profiles}</Button>
                    </Stack>
                    {settingsTab === "general" ? <SettingsForm t={t} /> : settingsTab === "vault" ? <VaultSettingsPage t={t} /> : settingsTab === "outbound" ? <OutboundPolicyPage t={t} /> : <ProfileEditor />}
                  </>
                ) : (
                  <Alert severity="error">{t.permission}</Alert>
                )
              ) : (
                active === "jobs" ? (
                  session.canManage ? <ManualJobEditor t={t} defaultZone={preferences.timeZone} /> : <Alert severity="error">{t.permission}</Alert>
                ) : active === "templates" ? (
                  <Paper variant="outlined" sx={{ p: 3, mb: 3, maxWidth: 900 }}>
                    <Stack spacing={2}>
                      <Typography component="h2" variant="h6">{t.templates}</Typography>
                      <TemplatePicker label={t.template} value={catalogItem} onChange={setCatalogItem} />
                      {catalogItem && <><Typography>{catalogItem.descriptor.description}</Typography><Typography variant="caption" color="text.secondary">{catalogItem.descriptor.id} · {catalogItem.descriptor.version}{catalogItem.selectable ? "" : " · retired"}</Typography></>}
                    </Stack>
                  </Paper>
                ) : (
                <Paper
                  variant="outlined"
                  sx={{
                    p: { xs: 3, sm: 4 },
                    mb: 3,
                    borderTop: "3px solid",
                    borderTopColor: "primary.main",
                  }}
                >
                  {active === "dashboard" && (
                    <Typography component="h2" variant="h6" sx={{ mb: 1 }}>
                      {t.welcome}
                    </Typography>
                  )}
                  <Typography
                    color="text.secondary"
                    sx={{ maxWidth: 660, lineHeight: 1.8 }}
                  >
                    {active === "dashboard" ? t.foundation : t.unavailable}
                  </Typography>
                </Paper>
                )
              )}
              <Paper variant="outlined" sx={{ p: 3, mt: 3 }}>
                <Typography component="h2" variant="h6" sx={{ mb: 1 }}>
                  {t.preferences}
                </Typography>
                <Typography
                  variant="body2"
                  color="text.secondary"
                  sx={{ mb: 3 }}
                >
                  {t.localPreferences}
                </Typography>
                <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
                  <TextField
                    select
                    label={t.language}
                    value={preferences.language}
                    onChange={(e) =>
                      preferences.setLanguage(
                        e.target.value === "pt-BR" ? "pt-BR" : "en",
                      )
                    }
                    slotProps={{ select: { native: true } }}
                    sx={{ minWidth: 180 }}
                  >
                    <option value="en">English</option>
                    <option value="pt-BR">Português (Brasil)</option>
                  </TextField>
                  <TextField
                    select
                    label={t.timeZone}
                    value={preferences.timeZone}
                    onChange={(e) => preferences.setTimeZone(e.target.value)}
                    slotProps={{ select: { native: true } }}
                    sx={{ minWidth: 200 }}
                  >
                    {timeZones.map((zone) => (
                      <option key={zone} value={zone}>
                        {zone}
                      </option>
                    ))}
                  </TextField>
                </Stack>
                <Typography
                  variant="body2"
                  color="text.secondary"
                  sx={{ mt: 2 }}
                >
                  {t.updated}:{" "}
                  {new Intl.DateTimeFormat(preferences.language, {
                    dateStyle: "medium",
                    timeStyle: "short",
                    timeZone: preferences.timeZone,
                  }).format(new Date())}
                </Typography>
              </Paper>
            </>
          ) : (
            <Alert severity="info" sx={{ mt: 3 }}>
              {t.signIn}
            </Alert>
          )}
        </AppShell>
      )}
    </ThemeProvider>
  );
}
