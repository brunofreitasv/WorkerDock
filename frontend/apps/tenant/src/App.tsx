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
} from "@mui/material";
import {
  AppShell,
  PageHeader,
  workerdockTheme,
  usePreferences,
  type NavigationGroup,
} from "@workerdock/ui";
import { useSession } from "./auth/SessionProvider";
import { signOut } from "./api/client";
import { VaultSettingsPage } from "./features/settings/VaultSettingsPage";
import { OutboundPolicyPage } from "./features/settings/OutboundPolicyPage";
import { JobManagementPage } from "./features/jobs/JobManagementPage";
import { JobHistoryTable } from "./features/jobs/JobHistoryTable";
import { ProfileEditor } from "./features/profiles/ProfileEditor";
import { TemplatePicker, type CatalogItem } from "./features/templates/TemplatePicker";
import { Dashboard } from "./features/monitoring/Dashboard";
import { JobsTable } from "./features/monitoring/JobsTable";
import { WorkersPage } from "./features/monitoring/WorkersPage";
import { ExecutionDetails } from "./features/monitoring/ExecutionDetails";
import { ExecutionHistoryPage } from "./features/monitoring/ExecutionHistoryPage";
import { AuditPage } from "./features/monitoring/AuditPage";
import { AlertRulesPage } from "./features/alerts/AlertRulesPage";
import RetentionPage from "./features/settings/RetentionPage";
import { HelpPage } from "./help/HelpPage";
import { helpTopics } from "./help/topics";
import { useTenantSettings } from "./features/settings/useTenantSettings";
import { TenantSettingsForm } from "./features/settings/TenantSettingsForm";
import { PersonalSettings } from "./features/settings/PersonalSettings";
import { SettingsNavigation, type SettingsTab } from "./features/settings/SettingsNavigation";
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
  const [settingsTab, setSettingsTab] = useState<SettingsTab>("personal");
  const [catalogItem, setCatalogItem] = useState<CatalogItem>();
  const [actionError, setActionError] = useState(false);
  const [jobsDirty, setJobsDirty] = useState(false);
  useEffect(() => {
    const change = () => {
      const next = routeFromHash(window.location.hash);
      if (jobsDirty && !window.confirm(t.jobDiscardConfirm)) {
        window.history.pushState(null, "", "#/" + active);
        return;
      }
      setJobsDirty(false);
      setActive(next);
    };
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, [jobsDirty, active, t.jobDiscardConfirm]);
  const navigate = (id: string) => {
    const route = id.startsWith("#/") ? id.slice(2) : id;
    if (jobsDirty && !window.confirm(t.jobDiscardConfirm)) return;
    setJobsDirty(false);
    setActive(route);
    window.history.pushState(null, "", "#/" + route);
  };
  const navigation: NavigationGroup[] = session?.canRead ? [
    { id: "monitoring", label: t.navMonitoring, items: [
      { id: "dashboard", label: t.dashboard },
      { id: "monitoring", label: t.jobMonitoring },
      { id: "workers", label: t.workers },
      { id: "alerts", label: t.alerts },
    ] },
    { id: "maintenance", label: t.navMaintenance, items: [
      { id: "jobs", label: t.jobs },
      { id: "templates", label: t.templates },
    ] },
    { id: "administration", label: t.navAdministration, items: [
      { id: "help", label: t.help },
      { id: "audit", label: t.audit },
      { id: "settings", label: t.settings },
    ] },
  ] : [];
  const flatNavigation = navigation.flatMap((group) => group.items);
  const activePage = active.startsWith("help:") ? "help" : active.startsWith("job-history:") ? "jobs" : active === "jobs" || active.startsWith("jobs/") || active.startsWith("jobs?") ? "jobs" : active;
  const activeNavigation = activePage.startsWith("execution:") ? "monitoring" : active.startsWith("job-history:") ? "jobs" : active === "executions" ? "dashboard" : activePage;
  const helpSlug = active.startsWith("help:") ? active.slice("help:".length) : undefined;
  const title = active.startsWith("job-history:") ? (preferences.language === "pt-BR" ? "Histórico da tarefa" : "Job history") : active.startsWith("execution:") ? "Execution details" : active === "executions" ? t.executionHistory : activePage === "help"
    ? helpTopics.find(topic => topic.slug === helpSlug)?.title[preferences.language] ?? t.help
    : flatNavigation.find((item) => item.id === activeNavigation)?.label ??
      (active === "settings" ? t.settings : t.dashboard);
  const contextualHelp = active === "dashboard" ? "dashboard"
    : active === "monitoring" || active === "executions" || active.startsWith("execution:") ? "monitoring"
    : active === "workers" ? "workers"
    : active === "audit" ? "audit"
    : active === "alerts" ? "alert-rules"
    : active === "templates" ? "templates"
    : activePage === "jobs" ? "jobs"
    : active === "settings" && settingsTab === "profiles" ? "sql-profiles"
    : active === "settings" ? "settings"
    : undefined;
  const contextualTopic = helpTopics.find(topic => topic.slug === contextualHelp);
  useEffect(() => { document.title = displayName; }, [displayName]);
  return (
    <ThemeProvider theme={workerdockTheme}>
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
          {activePage !== "help" && <PageHeader title={title} actions={contextualTopic && <Button onClick={() => navigate(`help:${contextualTopic.slug}`)}>{t.helpContextual}</Button>} />}
          {session.canRead ? (
            <>
              {activePage === "help" ? <HelpPage language={preferences.language} slug={helpSlug} canManage={session.canManage} onNavigate={navigate} /> : active === "settings" ? (
                <>
                  <SettingsNavigation t={t} selected={settingsTab} canManage={session.canManage} onSelect={setSettingsTab} />
                  {settingsTab === "personal" || !session.canManage ? <PersonalSettings t={t} mode={session.mode} canManage={session.canManage} language={preferences.language} setLanguage={preferences.setLanguage} timeZone={preferences.timeZone} setTimeZone={preferences.setTimeZone} />
                    : settingsTab === "general" ? <TenantSettingsForm t={t} current={tenantSettings.current} loading={tenantSettings.loading} error={tenantSettings.error} reload={tenantSettings.reload} acceptSave={tenantSettings.acceptSave} />
                    : settingsTab === "vault" ? <VaultSettingsPage t={t} />
                    : settingsTab === "outbound" ? <OutboundPolicyPage t={t} />
                    : settingsTab === "profiles" ? <ProfileEditor language={preferences.language} />
                    : <RetentionPage language={preferences.language} />}
                </>
              ) : (
              active.startsWith("job-history:") ? (
                  session.canManage ? <JobHistoryTable jobId={active.slice("job-history:".length)} language={preferences.language} timeZone={preferences.timeZone} /> : <Alert severity="error">{t.permission}</Alert>
                ) : activePage === "jobs" ? (
                  <Box component="section" aria-label={t.jobs} sx={{ display: "grid", gap: 3 }}>
                    <JobManagementPage t={t} route={active} canManage={session.canManage} defaultZone={preferences.timeZone} onNavigate={navigate} onDirtyChange={setJobsDirty} />
                  </Box>
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
                  <ExecutionDetails executionId={active.slice("execution:".length)} language={preferences.language} canManage={session.canManage} />
                ) : active === "executions" ? (
                  <ExecutionHistoryPage language={preferences.language} timeZone={preferences.timeZone} onExecution={id => navigate(`execution:${id}`)} />
                ) : active === "monitoring" ? (
                  <JobsTable language={preferences.language} canManage={session.canManage} onHistory={id => navigate(`job-history:${id}`)} />
                ) : active === "workers" ? (
                  <WorkersPage language={preferences.language} />
                ) : active === "audit" ? (
                  <AuditPage language={preferences.language} timeZone={preferences.timeZone} />
                ) : active === "alerts" ? (
                  <AlertRulesPage t={t} canManage={session.canManage} language={preferences.language} timeZone={preferences.timeZone} />
                ) : active === "dashboard" ? (
                  <Dashboard language={preferences.language} timeZone={preferences.timeZone} onHistory={() => navigate("executions")} onExecution={id => navigate(`execution:${id}`)} />
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
