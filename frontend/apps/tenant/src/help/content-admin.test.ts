import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";

const guides = ["settings", "retention", "troubleshooting"] as const;
const knownHelpSlugs = new Set([
  "getting-started", "first-job", "dashboard", "monitoring", "execution-events", "workers", "audit",
  "templates", "jobs", "sql-jobs", "http-jobs", "job-inputs", "schedules", "manual-runs", "execution-policies",
  "sql-profiles", "outbound-policy", "secrets", "webhook-sinks", "alert-rules", "incidents-deliveries", ...guides,
]);
const knownTenantRoutes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings", "#/profiles"]);

describe("administration guides", () => {
  it.each(["en", "pt-BR"] as const)("includes all three detailed, linked guides in %s", language => {
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
      expect(article).not.toMatch(/#\/help:(?:workers-audit|alerts)(?:\)|$)/);
    }
  });

  it("documents settings concurrency and archive safeguards", () => {
    const settings = getHelpArticle("en", "settings") ?? "";
    const retention = getHelpArticle("en", "retention") ?? "";
    expect(settings).toMatch(/Manage/);
    expect(settings).toMatch(/412|stale|changed elsewhere/i);
    expect(retention).toMatch(/30.*90.*365/);
    expect(retention).toMatch(/1.*3650/);
    expect(retention).toMatch(/preserves the corresponding source records/i);
    expect(retention).toMatch(/archive.*path|browser.*path/i);
  });
});
