import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";

const guides = ["sql-profiles", "outbound-policy", "secrets", "webhook-sinks", "alert-rules", "incidents-deliveries"] as const;
const helpSlugs = new Set([
  "getting-started", "first-job", "dashboard", "monitoring", "execution-events", "workers", "audit",
  "templates", "jobs", "sql-jobs", "http-jobs", "job-inputs", "schedules", "manual-runs", "execution-policies",
  ...guides, "settings", "retention", "troubleshooting",
]);
const routes = new Set(["#/dashboard", "#/monitoring", "#/jobs", "#/workers", "#/audit", "#/templates", "#/settings", "#/alerts", "#/help"]);

describe("integration and alert guides", () => {
  it.each(["en", "pt-BR"] as const)("includes all six complete, linked guides in %s", language => {
    for (const slug of guides) {
      const article = getHelpArticle(language, slug);
      expect(article, `${language}/${slug} exists`).toBeTruthy();
      expect(article).toMatch(/^## (Prerequisites|Pré-requisitos)$/m);
      expect(article).toMatch(/^## (Example|Worked example|Exemplo|Exemplo prático)$/m);
      expect(article).toMatch(/^## (Verify|Verification|Verificação)$/m);
      expect(article).toMatch(/^## (Recover|Recovery|Common mistakes and recovery|Recuperação|Erros comuns e recuperação)$/m);
      expect(article).toMatch(/^## (Related guides|Related topics|Guias relacionados|Tópicos relacionados)$/m);
      for (const [, route] of article!.matchAll(/\]\((#[^)]+)\)/g)) {
        const helpSlug = route.match(/^#\/help:([a-z0-9-]+)$/)?.[1];
        expect(routes.has(route) || Boolean(helpSlug && helpSlugs.has(helpSlug)), `${language}/${slug}: ${route}`).toBe(true);
      }
    }
  });

  it("keeps credential values out of guides and explains logical references", () => {
    for (const language of ["en", "pt-BR"] as const) {
      const secrets = getHelpArticle(language, "secrets")!.toLowerCase();
      const sink = getHelpArticle(language, "webhook-sinks")!.toLowerCase();
      expect(secrets).toMatch(language === "en" ? /secret reference/ : /referência.*segredo/);
      expect(secrets).toMatch(language === "en" ? /never.*(paste|enter|store).*secret/ : /nunca.*(cole|informe|armazene).*(senha|segredo|secreto)/);
      expect(sink).toMatch(language === "en" ? /secret reference/ : /referência.*segredo/);
      expect(secrets + sink).not.toMatch(/(?:password|token|secret)\s*[:=]\s*[^\s`]+/i);
    }
  });

  it("documents explicit alert activation and at-least-once delivery recovery", () => {
    for (const language of ["en", "pt-BR"] as const) {
      const rule = getHelpArticle(language, "alert-rules")!.toLowerCase();
      const delivery = getHelpArticle(language, "incidents-deliveries")!.toLowerCase();
      expect(rule).toMatch(language === "en" ? /disabled/ : /desativad/);
      expect(rule).toMatch(language === "en" ? /first save creates it \*\*disabled\*\*/ : /primeiro salvamento a cria \*\*desativada\*\*/);
      expect(rule).toMatch(language === "en" ? /enable/ : /ativar/);
      expect(delivery).toMatch(language === "en" ? /at least once/ : /pelo menos uma vez/);
      expect(delivery).toMatch(language === "en" ? /deduplicat/ : /deduplic/);
      expect(delivery).toMatch(language === "en" ? /retry|retried/ : /tentar novamente|repeti/);
    }
  });

  it("documents only built-in template version 1.1.0", () => {
    for (const language of ["en", "pt-BR"] as const) {
      for (const slug of ["templates", "job-inputs"] as const) {
        const article = getHelpArticle(language, slug)!;
        expect(article).toContain("1.1.0");
        expect(article).not.toContain("1.0.0");
      }
    }
  });
});
