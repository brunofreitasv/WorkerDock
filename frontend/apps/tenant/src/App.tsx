import { useEffect, useState } from "react";
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  CssBaseline,
  Paper,
  Stack,
  ThemeProvider,
  Typography,
  createTheme,
} from "@mui/material";
import {
  AppShell,
  usePreferences,
  type NavigationGroup,
} from "@workerdock/ui";
import { useSession } from "./auth/SessionProvider";
import { signOut } from "./api/client";
import { VaultSettingsPage } from "./features/settings/VaultSettingsPage";
import { OutboundPolicyPage } from "./features/settings/OutboundPolicyPage";
import { ManualJobEditor } from "./features/jobs/ManualJobEditor";
import { ProfileEditor } from "./features/profiles/ProfileEditor";
import { TemplatePicker, type CatalogItem } from "./features/templates/TemplatePicker";
import { Dashboard } from "./features/monitoring/Dashboard";
import { JobsTable } from "./features/monitoring/JobsTable";
import { WorkersPage } from "./features/monitoring/WorkersPage";
import { ExecutionDetails } from "./features/monitoring/ExecutionDetails";
import { AuditPage } from "./features/monitoring/AuditPage";
import { AlertRulesPage } from "./features/alerts/AlertRulesPage";
import RetentionPage from "./features/settings/RetentionPage";
import { HelpPage } from "./help/HelpPage";
import { helpTopics } from "./help/topics";
import { useTenantSettings } from "./features/settings/useTenantSettings";
import { TenantSettingsForm } from "./features/settings/TenantSettingsForm";
import { PersonalSettings } from "./features/settings/PersonalSettings";
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
function routeFromHash(hash: string): string {
  if (!hash.startsWith("#/")) return "dashboard";
  return hash.slice(2) || "dashboard";
}

export default function App() {
  const { session, loading, error, retry } = useSession();
  const preferences = usePreferences(session?.timeZone);
  const { t } = preferences;
  const tenantSettings = useTenantSettings(Boolean(session?.canRead));
  const displayName = tenantSettings.current?.data.displayName || "WorkerDock";
  const [active, setActive] = useState(
    () => routeFromHash(window.location.hash),
  );
  const [settingsTab, setSettingsTab] = useState("personal");
  const [catalogItem, setCatalogItem] = useState<CatalogItem>();
  const [actionError, setActionError] = useState(false);
  useEffect(() => {
    const change = () => setActive(routeFromHash(window.location.hash));
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);
  const navigate = (id: string) => {
    const route = id.startsWith("#/") ? id.slice(2) : id;
    setActive(route);
    window.history.replaceState(null, "", "#/" + route);
  };
  const navigation: NavigationGroup[] = session?.canRead ? [
    { id: "monitoring", label: t.navMonitoring, items: [
      { id: "dashboard", label: t.dashboard },
      { id: "monitoring", label: t.jobMonitoring },
      { id: "workers", label: t.workers },
      { id: "alerts", label: t.alerts },
    ] },
    { id: "maintenance", label: t.navMaintenance, items: [
      ...(session.canManage ? [{ id: "jobs", label: t.jobs }] : []),
      { id: "templates", label: t.templates },
    ] },
    { id: "administration", label: t.navAdministration, items: [
      { id: "help", label: t.help },
      { id: "audit", label: t.audit },
      { id: "settings", label: t.settings },
    ] },
  ] : [];
  const flatNavigation = navigation.flatMap((group) => group.items);
  const activePage = active.startsWith("help:") ? "help" : active;
  const activeNavigation = activePage.startsWith("execution:") ? "monitoring" : activePage;
  const helpSlug = active.startsWith("help:") ? active.slice("help:".length) : undefined;
  const title = active.startsWith("execution:") ? "Execution details" : activePage === "help"
    ? helpTopics.find(topic => topic.slug === helpSlug)?.title[preferences.language] ?? t.help
    : flatNavigation.find((item) => item.id === activeNavigation)?.label ??
      (active === "settings" ? t.settings : t.dashboard);
  const contextualHelp = active === "dashboard" ? "dashboard"
    : active === "monitoring" || active.startsWith("execution:") ? "monitoring"
    : active === "workers" ? "workers"
    : active === "audit" ? "audit"
    : active === "alerts" ? "alert-rules"
    : active === "templates" ? "templates"
    : active === "jobs" ? "jobs"
    : active === "settings" && settingsTab === "profiles" ? "sql-profiles"
    : active === "settings" ? "settings"
    : undefined;
  const contextualTopic = helpTopics.find(topic => topic.slug === contextualHelp);
  useEffect(() => { document.title = displayName; }, [displayName]);
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
          displayName={displayName}
          navigation={navigation}
          active={activeNavigation}
          onNavigate={navigate}
          mode={session.mode}
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
          {activePage !== "help" && <Typography component="h1" variant="h4" sx={{ mb: 1 }}>
            {title}
          </Typography>}
          {activePage !== "help" && contextualTopic && <Button onClick={() => navigate(`help:${contextualTopic.slug}`)} sx={{ mb: 2 }}>{t.helpContextual}</Button>}
          {session.canRead ? (
            <>
              {activePage === "help" ? <HelpPage language={preferences.language} slug={helpSlug} canManage={session.canManage} onNavigate={navigate} /> : active === "settings" ? (
                <>
                  <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mb: 3, flexWrap: "wrap" }}>
                    <Button variant={settingsTab === "personal" ? "contained" : "outlined"} onClick={() => setSettingsTab("personal")}>{t.personalSettings}</Button>
                    {session.canManage && <>
                      <Button variant={settingsTab === "general" ? "contained" : "outlined"} onClick={() => setSettingsTab("general")}>{t.tenantConfiguration}</Button>
                      <Button variant={settingsTab === "vault" ? "contained" : "outlined"} onClick={() => setSettingsTab("vault")}>{t.vaultSettings}</Button>
                      <Button variant={settingsTab === "outbound" ? "contained" : "outlined"} onClick={() => setSettingsTab("outbound")}>{t.outboundSettings}</Button>
                      <Button variant={settingsTab === "profiles" ? "contained" : "outlined"} onClick={() => setSettingsTab("profiles")}>{t.profiles}</Button>
                      <Button variant={settingsTab === "retention" ? "contained" : "outlined"} onClick={() => setSettingsTab("retention")}>{t.retention}</Button>
                    </>}
                  </Stack>
                  {settingsTab === "personal" || !session.canManage ? <PersonalSettings t={t} mode={session.mode} canManage={session.canManage} language={preferences.language} setLanguage={preferences.setLanguage} timeZone={preferences.timeZone} setTimeZone={preferences.setTimeZone} />
                    : settingsTab === "general" ? <TenantSettingsForm t={t} current={tenantSettings.current} loading={tenantSettings.loading} error={tenantSettings.error} reload={tenantSettings.reload} acceptSave={tenantSettings.acceptSave} />
                    : settingsTab === "vault" ? <VaultSettingsPage t={t} />
                    : settingsTab === "outbound" ? <OutboundPolicyPage t={t} />
                    : settingsTab === "profiles" ? <ProfileEditor />
                    : <RetentionPage />}
                </>
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
                active.startsWith("execution:") ? (
                  <ExecutionDetails executionId={active.slice("execution:".length)} language={preferences.language} />
                ) : active === "monitoring" ? (
                  <JobsTable language={preferences.language} />
                ) : active === "workers" ? (
                  <WorkersPage language={preferences.language} />
                ) : active === "audit" ? (
                  <AuditPage language={preferences.language} />
                ) : active === "alerts" ? (
                  <AlertRulesPage t={t} canManage={session.canManage} />
                ) : active === "dashboard" ? (
                  <Dashboard language={preferences.language} onExecution={id => navigate(`execution:${id}`)} />
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
                  <Typography color="text.secondary" sx={{ maxWidth: 660, lineHeight: 1.8 }}>{t.unavailable}</Typography>
                </Paper>
                )
              ))}
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
