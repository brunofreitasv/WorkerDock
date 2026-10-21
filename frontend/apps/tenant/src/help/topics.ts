import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";

export type HelpGroupId =
  | "start-here"
  | "operate-monitor"
  | "build-run-jobs"
  | "integrations-alerts"
  | "administration";

export type HelpGroup = {
  id: HelpGroupId;
  title: Record<Language, string>;
};

export type HelpTopic = {
  slug: string;
  group: HelpGroupId;
  title: Record<Language, string>;
  summary: Record<Language, string>;
  keywords: Record<Language, readonly string[]>;
  route: string;
  requiredRole: "Read" | "Manage";
  related: readonly string[];
  /** Kept for the current Help page while its presentation is being updated. */
  category: Record<Language, string>;
};

export const helpGroups: readonly HelpGroup[] = [
  { id: "start-here", title: { en: "Start here", "pt-BR": "Comece aqui" } },
  {
    id: "operate-monitor",
    title: { en: "Operate and monitor", "pt-BR": "Operação e monitoramento" },
  },
  {
    id: "build-run-jobs",
    title: {
      en: "Build and run jobs",
      "pt-BR": "Criação e execução de tarefas",
    },
  },
  {
    id: "integrations-alerts",
    title: { en: "Integrations and alerts", "pt-BR": "Integrações e alertas" },
  },
  {
    id: "administration",
    title: { en: "Administration", "pt-BR": "Administração" },
  },
];

type TopicSeed = Omit<HelpTopic, "category">;
const seeds: readonly TopicSeed[] = [
  {
    slug: "getting-started",
    group: "start-here",
    title: { en: "Get oriented", "pt-BR": "Conheça o WorkerDock" },
    summary: {
      en: "Learn the tenant workspace, navigation, and Read and Manage access.",
      "pt-BR":
        "Conheça o espaço do tenant, a navegação e os acessos de Leitura e Gerenciar.",
    },
    keywords: {
      en: ["orientation", "permissions", "access", "tenant"],
      "pt-BR": ["orientação", "permissões", "acesso", "tenant"],
    },
    route: "#/dashboard",
    requiredRole: "Read",
    related: ["first-job", "dashboard", "templates"],
  },
  {
    slug: "first-job",
    group: "start-here",
    title: { en: "Run your first job", "pt-BR": "Execute sua primeira tarefa" },
    summary: {
      en: "Follow a safe walkthrough from an installed template to a verified run.",
      "pt-BR":
        "Siga um passo a passo seguro de um modelo instalado até uma execução verificada.",
    },
    keywords: {
      en: ["first job", "walkthrough", "run", "example"],
      "pt-BR": ["primeira tarefa", "passo a passo", "executar", "exemplo"],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["templates", "jobs", "manual-runs"],
  },
  {
    slug: "dashboard",
    group: "operate-monitor",
    title: { en: "Understand the dashboard", "pt-BR": "Entenda o painel" },
    summary: {
      en: "Read workload, capacity, upcoming work, and data freshness at a glance.",
      "pt-BR":
        "Consulte a carga de trabalho, a capacidade, os próximos trabalhos e a atualização dos dados.",
    },
    keywords: {
      en: ["overview", "capacity", "refresh", "upcoming"],
      "pt-BR": ["visão geral", "capacidade", "atualização", "próximos"],
    },
    route: "#/dashboard",
    requiredRole: "Read",
    related: ["monitoring", "execution-events", "workers"],
  },
  {
    slug: "monitoring",
    group: "operate-monitor",
    title: { en: "Monitor executions", "pt-BR": "Monitore execuções" },
    summary: {
      en: "Inspect execution status, timing, and stale monitoring data.",
      "pt-BR":
        "Inspecione o estado, a duração e os dados desatualizados das execuções.",
    },
    keywords: {
      en: ["monitor", "execution", "status", "stale data"],
      "pt-BR": ["monitoramento", "execução", "estado", "dados desatualizados"],
    },
    route: "#/monitoring",
    requiredRole: "Read",
    related: ["dashboard", "execution-events", "manual-runs"],
  },
  {
    slug: "execution-events",
    group: "operate-monitor",
    title: { en: "Read logs and events", "pt-BR": "Consulte logs e eventos" },
    summary: {
      en: "Use the event timeline and logs to investigate an execution.",
      "pt-BR": "Use a linha do tempo e os logs para investigar uma execução.",
    },
    keywords: {
      en: ["logs", "events", "timeline", "diagnostics"],
      "pt-BR": ["logs", "eventos", "linha do tempo", "diagnóstico"],
    },
    route: "#/monitoring",
    requiredRole: "Read",
    related: ["monitoring", "workers", "audit"],
  },
  {
    slug: "workers",
    group: "operate-monitor",
    title: {
      en: "Check worker health",
      "pt-BR": "Verifique a saúde dos workers",
    },
    summary: {
      en: "Review worker availability and understand heartbeat information.",
      "pt-BR":
        "Consulte a disponibilidade dos workers e entenda os dados de heartbeat.",
    },
    keywords: {
      en: ["workers", "health", "heartbeat", "availability"],
      "pt-BR": ["workers", "saúde", "heartbeat", "disponibilidade"],
    },
    route: "#/workers",
    requiredRole: "Read",
    related: ["monitoring", "execution-events", "audit"],
  },
  {
    slug: "audit",
    group: "operate-monitor",
    title: {
      en: "Review audit history",
      "pt-BR": "Consulte o histórico de auditoria",
    },
    summary: {
      en: "Find tenant changes and identify who performed an action.",
      "pt-BR":
        "Encontre alterações no tenant e identifique quem realizou uma ação.",
    },
    keywords: {
      en: ["audit", "history", "changes", "actor"],
      "pt-BR": ["auditoria", "histórico", "alterações", "responsável"],
    },
    route: "#/audit",
    requiredRole: "Read",
    related: ["workers", "settings", "execution-events"],
  },
  {
    slug: "templates",
    group: "build-run-jobs",
    title: {
      en: "Browse installed templates",
      "pt-BR": "Explore modelos instalados",
    },
    summary: {
      en: "Compare installed templates and choose one for a job.",
      "pt-BR": "Compare os modelos instalados e escolha um para uma tarefa.",
    },
    keywords: {
      en: ["templates", "installed", "custom template", "catalog"],
      "pt-BR": ["modelos", "instalados", "modelo personalizado", "catálogo"],
    },
    route: "#/templates",
    requiredRole: "Read",
    related: ["first-job", "jobs", "job-inputs"],
  },
  {
    slug: "jobs",
    group: "build-run-jobs",
    title: { en: "Create and manage jobs", "pt-BR": "Crie e gerencie tarefas" },
    summary: {
      en: "Create a job, validate its settings, activate it, and manage its lifecycle.",
      "pt-BR":
        "Crie uma tarefa, valide as configurações, ative-a e gerencie seu ciclo de vida.",
    },
    keywords: {
      en: ["job", "draft", "validation", "activation", "archive"],
      "pt-BR": ["tarefa", "rascunho", "validação", "ativação", "arquivar"],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["templates", "job-inputs", "schedules"],
  },
  {
    slug: "sql-jobs",
    group: "build-run-jobs",
    title: {
      en: "Configure SQL Server jobs",
      "pt-BR": "Configure tarefas do SQL Server",
    },
    summary: {
      en: "Set up SQL queries, profiles, and typed parameters for a job.",
      "pt-BR":
        "Configure consultas SQL, perfis e parâmetros tipados para uma tarefa.",
    },
    keywords: {
      en: ["SQL", "SQL Server", "query", "parameters"],
      "pt-BR": ["SQL", "SQL Server", "consulta", "parâmetros"],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["sql-profiles", "job-inputs", "execution-policies"],
  },
  {
    slug: "http-jobs",
    group: "build-run-jobs",
    title: { en: "Configure HTTP jobs", "pt-BR": "Configure tarefas HTTP" },
    summary: {
      en: "Configure a request, destination policy, and secret-backed headers.",
      "pt-BR":
        "Configure uma solicitação, a política de destino e cabeçalhos com segredos.",
    },
    keywords: {
      en: ["HTTP", "request", "webhook", "headers", "allowlist"],
      "pt-BR": [
        "HTTP",
        "solicitação",
        "webhook",
        "cabeçalhos",
        "lista permitida",
      ],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["outbound-policy", "secrets", "execution-policies"],
  },
  {
    slug: "job-inputs",
    group: "build-run-jobs",
    title: {
      en: "Set job inputs and dynamic values",
      "pt-BR": "Defina entradas e valores dinâmicos",
    },
    summary: {
      en: "Choose input modes and preview supported dynamic values safely.",
      "pt-BR":
        "Escolha modos de entrada e visualize valores dinâmicos compatíveis com segurança.",
    },
    keywords: {
      en: ["inputs", "parameters", "dynamic values", "preview"],
      "pt-BR": ["entradas", "parâmetros", "valores dinâmicos", "visualização"],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["jobs", "sql-jobs", "http-jobs"],
  },
  {
    slug: "schedules",
    group: "build-run-jobs",
    title: {
      en: "Set schedules and time zones",
      "pt-BR": "Configure agendas e fusos horários",
    },
    summary: {
      en: "Schedule work and understand time zones, overlaps, and missed runs.",
      "pt-BR":
        "Agende tarefas e entenda fusos horários, sobreposições e execuções perdidas.",
    },
    keywords: {
      en: ["schedule", "cron", "time zone", "overlap", "misfire"],
      "pt-BR": [
        "agenda",
        "cron",
        "fuso horário",
        "sobreposição",
        "falha de horário",
      ],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["jobs", "manual-runs", "execution-policies"],
  },
  {
    slug: "manual-runs",
    group: "build-run-jobs",
    title: {
      en: "Start and review manual runs",
      "pt-BR": "Inicie e consulte execuções manuais",
    },
    summary: {
      en: "Start an authorized run and verify its execution record.",
      "pt-BR":
        "Inicie uma execução autorizada e verifique o registro da execução.",
    },
    keywords: {
      en: ["manual", "run now", "execute", "verification"],
      "pt-BR": ["manual", "executar agora", "execução", "verificação"],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["jobs", "monitoring", "execution-policies"],
  },
  {
    slug: "execution-policies",
    group: "build-run-jobs",
    title: {
      en: "Understand execution policies",
      "pt-BR": "Entenda as políticas de execução",
    },
    summary: {
      en: "Review retry, concurrency, timeout, and failure handling settings.",
      "pt-BR":
        "Consulte as configurações de repetição, concorrência, tempo limite e tratamento de falhas.",
    },
    keywords: {
      en: ["execution policy", "retry", "concurrency", "timeout"],
      "pt-BR": [
        "política de execução",
        "repetição",
        "concorrência",
        "tempo limite",
      ],
    },
    route: "#/jobs",
    requiredRole: "Manage",
    related: ["jobs", "schedules", "manual-runs"],
  },
  {
    slug: "sql-profiles",
    group: "integrations-alerts",
    title: {
      en: "Manage SQL Server profiles",
      "pt-BR": "Gerencie perfis do SQL Server",
    },
    summary: {
      en: "Create versioned SQL profiles with catalog allowlists and secret references.",
      "pt-BR":
        "Crie perfis SQL versionados com listas de catálogos e referências de segredo.",
    },
    keywords: {
      en: ["SQL", "profiles", "catalog", "password", "diagnostic"],
      "pt-BR": ["SQL", "perfis", "catálogo", "senha", "diagnóstico"],
    },
    route: "#/settings",
    requiredRole: "Manage",
    related: ["sql-jobs", "secrets", "settings"],
  },
  {
    slug: "outbound-policy",
    group: "integrations-alerts",
    title: {
      en: "Control outbound destinations",
      "pt-BR": "Controle destinos de saída",
    },
    summary: {
      en: "Allow only approved outbound hosts for HTTP work and webhook delivery.",
      "pt-BR":
        "Permita somente hosts de saída aprovados para tarefas HTTP e entrega de webhooks.",
    },
    keywords: {
      en: ["outbound", "hosts", "allowlist", "destinations", "network"],
      "pt-BR": ["saída", "hosts", "lista permitida", "destinos", "rede"],
    },
    route: "#/settings",
    requiredRole: "Manage",
    related: ["http-jobs", "webhook-sinks", "secrets"],
  },
  {
    slug: "secrets",
    group: "integrations-alerts",
    title: {
      en: "Store and reference secrets",
      "pt-BR": "Armazene e referencie segredos",
    },
    summary: {
      en: "Use logical secret references without exposing credential values.",
      "pt-BR":
        "Use referências lógicas de segredos sem expor valores de credenciais.",
    },
    keywords: {
      en: ["secrets", "credentials", "Vault", "password", "token"],
      "pt-BR": ["segredos", "credenciais", "cofre", "senha", "token"],
    },
    route: "#/settings",
    requiredRole: "Manage",
    related: ["sql-profiles", "http-jobs", "webhook-sinks"],
  },
  {
    slug: "webhook-sinks",
    group: "integrations-alerts",
    title: {
      en: "Configure webhook sinks",
      "pt-BR": "Configure destinos de webhook",
    },
    summary: {
      en: "Set up and enable an outbound webhook destination safely.",
      "pt-BR":
        "Configure e habilite um destino de webhook de saída com segurança.",
    },
    keywords: {
      en: ["webhook", "sink", "delivery", "secret", "outbound"],
      "pt-BR": ["webhook", "destino", "entrega", "segredo", "saída"],
    },
    route: "#/alerts",
    requiredRole: "Manage",
    related: ["alert-rules", "outbound-policy", "secrets"],
  },
  {
    slug: "alert-rules",
    group: "integrations-alerts",
    title: { en: "Create alert rules", "pt-BR": "Crie regras de alerta" },
    summary: {
      en: "Define alert conditions and route matching incidents to a sink.",
      "pt-BR":
        "Defina condições de alerta e encaminhe incidentes correspondentes a um destino.",
    },
    keywords: {
      en: ["alerts", "rules", "conditions", "incident", "sink"],
      "pt-BR": ["alertas", "regras", "condições", "incidente", "destino"],
    },
    route: "#/alerts",
    requiredRole: "Manage",
    related: ["webhook-sinks", "incidents-deliveries", "monitoring"],
  },
  {
    slug: "incidents-deliveries",
    group: "integrations-alerts",
    title: {
      en: "Review incidents and deliveries",
      "pt-BR": "Consulte incidentes e entregas",
    },
    summary: {
      en: "Trace an incident through its webhook delivery status and recovery.",
      "pt-BR":
        "Acompanhe um incidente pelo estado de entrega do webhook e sua recuperação.",
    },
    keywords: {
      en: ["incidents", "deliveries", "webhook", "retry", "status"],
      "pt-BR": ["incidentes", "entregas", "webhook", "repetição", "estado"],
    },
    route: "#/alerts",
    requiredRole: "Manage",
    related: ["alert-rules", "webhook-sinks", "execution-events"],
  },
  {
    slug: "settings",
    group: "administration",
    title: {
      en: "Manage tenant settings",
      "pt-BR": "Gerencie as configurações do tenant",
    },
    summary: {
      en: "Review tenant defaults, configuration, and access requirements.",
      "pt-BR":
        "Consulte os padrões do tenant, as configurações e os requisitos de acesso.",
    },
    keywords: {
      en: ["tenant", "settings", "configuration", "defaults"],
      "pt-BR": ["tenant", "configurações", "configuração", "padrões"],
    },
    route: "#/settings",
    requiredRole: "Manage",
    related: ["retention", "audit", "sql-profiles"],
  },
  {
    slug: "retention",
    group: "administration",
    title: {
      en: "Manage retention and archives",
      "pt-BR": "Gerencie retenção e arquivos",
    },
    summary: {
      en: "Understand data retention, archived jobs, and available history.",
      "pt-BR":
        "Entenda a retenção de dados, tarefas arquivadas e o histórico disponível.",
    },
    keywords: {
      en: ["retention", "archives", "history", "cleanup"],
      "pt-BR": ["retenção", "arquivos", "histórico", "limpeza"],
    },
    route: "#/settings",
    requiredRole: "Manage",
    related: ["settings", "audit", "jobs"],
  },
  {
    slug: "troubleshooting",
    group: "administration",
    title: {
      en: "Troubleshoot common problems",
      "pt-BR": "Resolva problemas comuns",
    },
    summary: {
      en: "Diagnose common access, configuration, and execution problems.",
      "pt-BR":
        "Diagnostique problemas comuns de acesso, configuração e execução.",
    },
    keywords: {
      en: ["troubleshooting", "error", "failure", "diagnostic"],
      "pt-BR": ["solução de problemas", "erro", "falha", "diagnóstico"],
    },
    route: "#/dashboard",
    requiredRole: "Read",
    related: ["monitoring", "execution-events", "settings"],
  },
];

const groupById = new Map(helpGroups.map((group) => [group.id, group]));
export const helpTopics: readonly HelpTopic[] = seeds.map((topic) => ({
  ...topic,
  category: groupById.get(topic.group)!.title,
}));

const aliases: Readonly<Record<string, string>> = {
  "workers-audit": "workers",
  alerts: "alert-rules",
};
const topicsBySlug = new Map(helpTopics.map((topic) => [topic.slug, topic]));

export function resolveHelpTopic(
  slug: string | undefined,
): HelpTopic | undefined {
  if (!slug || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug)) return undefined;
  return topicsBySlug.get(aliases[slug] ?? slug);
}

function normalize(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase();
}

/** Compatibility search for the existing Help page; the richer search API lives in search.ts. */
export function searchHelpTopics(
  language: Language,
  query: string,
): HelpTopic[] {
  const terms = normalize(query.trim()).split(/\s+/).filter(Boolean);
  if (!terms.length) return [...helpTopics];
  return helpTopics.filter((topic) => {
    const body = getHelpArticle(language, topic.slug) ?? "";
    const text = normalize(
      [
        topic.title[language],
        topic.summary[language],
        ...topic.keywords[language],
        body,
      ].join(" "),
    );
    return terms.every((term) => text.includes(term));
  });
}
