import { Alert } from "@mui/material";
import type { Labels, Language } from "@workerdock/ui";
import { JobWizard } from "./JobWizard";
import { JobInventory } from "./JobInventory";
import { ManagedJobDetail } from "./ManagedJobDetail";

export function JobManagementPage({ t, route, canManage, defaultZone, language = "en", onNavigate, onDirtyChange }: { t: Labels; route: string; canManage: boolean; defaultZone: string; language?: Language; onNavigate(route: string): void; onDirtyChange?(dirty: boolean): void }) {
  const path = route.split("?", 1)[0];
  if (path === "jobs/new") return canManage ? <JobWizard t={t} defaultZone={defaultZone} language={language} /> : <Alert severity="error">{t.permission}</Alert>;
  if (path.startsWith("jobs/") && path !== "jobs/") {
    const params = new URLSearchParams(route.includes("?") ? route.slice(route.indexOf("?") + 1) : "");
    const view = params.get("returnView");
    const search = params.get("returnSearch");
    const returnRoute = `jobs${view || search ? `?view=${encodeURIComponent(view ?? "current")}${search ? `&search=${encodeURIComponent(search)}` : ""}` : ""}`;
    return <ManagedJobDetail jobId={path.slice("jobs/".length)} returnRoute={returnRoute} t={t} language={language} clientTimeZone={defaultZone} canManage={canManage} onNavigate={onNavigate} onDirtyChange={onDirtyChange} />;
  }
  return <JobInventory t={t} route={route} canManage={canManage} onNavigate={onNavigate} />;
}
