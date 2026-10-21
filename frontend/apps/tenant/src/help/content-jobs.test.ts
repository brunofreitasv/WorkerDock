import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";

const guides = ["templates", "jobs", "sql-jobs", "http-jobs", "job-inputs", "schedules", "manual-runs", "execution-policies"] as const;
const knownHelpSlugs = new Set([
  "getting-started", "first-job", "dashboard", "monitoring", "execution-events", "workers", "audit",
  ...guides, "sql-profiles", "outbound-policy", "secrets", "webhook-sinks", "alert-rules",
  "incidents-deliveries", "settings", "retention", "troubleshooting",
]);
const knownTenantRoutes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings", "#/profiles"]);

describe("build and run job guides", () => {
  it.each(["en", "pt-BR"] as const)("includes all eight complete, linked guides in %s", language => {
    for (const slug of guides) {
      const article = getHelpArticle(language, slug);
      expect(article, `${language}/${slug} exists`).toBeTruthy();
      expect(article).toMatch(/^## (Prerequisites|Pré-requisitos)$/m);
      expect(article).toMatch(/^## (Role|Função|Permissão)$/m);
      expect(article).toMatch(/^## (Worked example|Example|Exemplo prático|Exemplo)$/m);
      expect(article).toMatch(/^## (Verify|Verification|Verificação)$/m);
      expect(article).toMatch(/^## (Recover|Recovery|Common mistakes and recovery|Recuperação|Erros comuns e recuperação)$/m);
      expect(article).toMatch(/^## (Related guides|Related topics|Guias relacionados|Tópicos relacionados)$/m);

      for (const [, route] of article!.matchAll(/\]\((#[^)]+)\)/g)) {
        const helpSlug = route.match(/^#\/help:([a-z0-9-]+)$/)?.[1];
        expect(knownTenantRoutes.has(route) || Boolean(helpSlug && knownHelpSlugs.has(helpSlug)), `${language}/${slug}: ${route}`).toBe(true);
      }
    }
  });

  it.each(["en", "pt-BR"] as const)("references template authoring as a source workflow in %s", language => {
    const article = getHelpArticle(language, "templates") ?? "";
    expect(article).toContain("docs/how-to/add-custom-template.md");
    expect(article).toMatch(/developer change|developer.*deploy|desenvolvedor|desenvolvimento.*implanta/i);
    expect(article).not.toMatch(/\]\([^)]*add-custom-template\.md/i);
    expect(article).not.toMatch(/\]\(#[^)]*(create-template|new-template|templates\/new)/i);
  });
});
