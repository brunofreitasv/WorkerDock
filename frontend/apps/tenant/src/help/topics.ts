import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";

export type HelpTopic = {
  slug: string;
  title: Record<Language, string>;
  category: Record<Language, string>;
  route: string;
  requiredRole: "Read" | "Manage";
};

export const helpTopics: readonly HelpTopic[] = [
  { slug: "getting-started", title: { en: "Getting started", "pt-BR": "Primeiros passos" }, category: { en: "Basics", "pt-BR": "Noções básicas" }, route: "#/dashboard", requiredRole: "Read" },
  { slug: "dashboard", title: { en: "Understand the dashboard", "pt-BR": "Entenda o painel" }, category: { en: "Monitoring", "pt-BR": "Monitoramento" }, route: "#/dashboard", requiredRole: "Read" },
  { slug: "monitoring", title: { en: "Monitor jobs and executions", "pt-BR": "Monitore tarefas e execuções" }, category: { en: "Monitoring", "pt-BR": "Monitoramento" }, route: "#/monitoring", requiredRole: "Read" },
  { slug: "workers-audit", title: { en: "Review workers and audit events", "pt-BR": "Consulte workers e eventos de auditoria" }, category: { en: "Monitoring", "pt-BR": "Monitoramento" }, route: "#/workers", requiredRole: "Read" },
  { slug: "alerts", title: { en: "Set up alerts", "pt-BR": "Configure alertas" }, category: { en: "Operations", "pt-BR": "Operações" }, route: "#/alerts", requiredRole: "Manage" },
  { slug: "sql-profiles", title: { en: "Manage SQL Server profiles", "pt-BR": "Gerencie perfis do SQL Server" }, category: { en: "Integrations", "pt-BR": "Integrações" }, route: "#/settings", requiredRole: "Manage" },
  { slug: "templates", title: { en: "Browse installed templates", "pt-BR": "Explore modelos instalados" }, category: { en: "Jobs", "pt-BR": "Tarefas" }, route: "#/templates", requiredRole: "Read" },
  { slug: "jobs", title: { en: "Create and manage jobs", "pt-BR": "Crie e gerencie tarefas" }, category: { en: "Jobs", "pt-BR": "Tarefas" }, route: "#/jobs", requiredRole: "Manage" },
  { slug: "schedules", title: { en: "Manage schedules", "pt-BR": "Gerencie agendamentos" }, category: { en: "Jobs", "pt-BR": "Tarefas" }, route: "#/jobs", requiredRole: "Manage" },
  { slug: "sql-jobs", title: { en: "Configure SQL Server jobs", "pt-BR": "Configure tarefas do SQL Server" }, category: { en: "Jobs", "pt-BR": "Tarefas" }, route: "#/jobs", requiredRole: "Manage" },
  { slug: "http-jobs", title: { en: "Configure HTTP jobs", "pt-BR": "Configure tarefas HTTP" }, category: { en: "Jobs", "pt-BR": "Tarefas" }, route: "#/jobs", requiredRole: "Manage" },
  { slug: "settings", title: { en: "Manage tenant settings", "pt-BR": "Gerencie as configurações do tenant" }, category: { en: "Administration", "pt-BR": "Administração" }, route: "#/settings", requiredRole: "Manage" },
];

function normalize(value: string): string {
  return value.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLocaleLowerCase();
}

export function searchHelpTopics(language: Language, query: string): HelpTopic[] {
  const needle = normalize(query.trim());
  if (!needle) return [...helpTopics];
  return helpTopics.filter(topic => {
    const body = getHelpArticle(language, topic.slug) ?? "";
    return normalize(`${topic.title[language]} ${topic.category[language]} ${body}`).includes(needle);
  });
}
