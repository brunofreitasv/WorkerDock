import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";

const slugs = ["getting-started", "first-job", "dashboard", "monitoring", "execution-events", "workers", "audit"];
const canonicalSlugs = new Set(["getting-started", "first-job", "dashboard", "monitoring", "execution-events", "workers", "audit", "templates", "jobs", "sql-jobs", "http-jobs", "job-inputs", "schedules", "manual-runs", "execution-policies", "sql-profiles", "outbound-policy", "secrets", "webhook-sinks", "alert-rules", "incidents-deliveries", "settings", "retention", "troubleshooting"]);
const taskHeadings = {
  en: ["What you will do", "Before you start", "Steps", "Example", "Check the result", "If it does not work", "Related guides"],
  "pt-BR": ["O que você vai fazer", "Antes de começar", "Etapas", "Exemplo", "Verifique o resultado", "Se não funcionar", "Guias relacionados"],
} as const;
const knownRoutes = new Set(["#/dashboard", "#/monitoring", "#/executions", "#/jobs", "#/workers", "#/audit", "#/templates", "#/settings", "#/alerts", "#/help"]);

describe("start and monitoring guide content", () => {
  for (const slug of slugs) {
    it(`${slug} is complete in both languages and uses resolvable internal links`, () => {
      for (const language of ["en", "pt-BR"] as const) {
        const article = getHelpArticle(language, slug);
        expect(article, `${language}/${slug} exists`).toBeTruthy();
        const headings = taskHeadings[language].filter(heading => heading !== "Steps" && heading !== "Etapas");
        for (const heading of headings) {
          expect(article, `${language}/${slug} includes ${heading}`).toContain(`## ${heading}`);
        }
        const stepHeading = language === "en" ? "## Steps" : "## Etapas";
        const overviewHeading = language === "en" ? "## Explore the screen" : "## Explore a tela";
        expect(article, `${language}/${slug} includes task steps or an overview`).toMatch(new RegExp(`${stepHeading}|${overviewHeading}`));
        for (const [, target] of article!.matchAll(/\[[^\]]+\]\((#[^)]+)\)/g)) {
          if (target.startsWith("#/help:")) {
            expect(canonicalSlugs, `${language}/${slug} links to ${target}`).toContain(target.slice("#/help:".length));
          } else {
            expect(knownRoutes, `${language}/${slug} links to ${target}`).toContain(target);
          }
        }
      }
    });
  }

  it("explains read access, worker and audit availability, and stale monitoring data", () => {
    for (const language of ["en", "pt-BR"] as const) {
      const article = (slug: string) => getHelpArticle(language, slug)!.toLocaleLowerCase();
      const allContent = slugs.map(article).join("\n");
      expect(allContent).toContain(language === "en" ? "manage" : "gerenciar");
      for (const slug of ["workers", "audit"]) {
        const content = article(slug);
        expect(content, `${language}/${slug} explains read access`).toContain(language === "en" ? "read access" : "leitura");
        expect(content, `${language}/${slug} explains unavailable data`).toContain(language === "en" ? "unavailable" : "indispon");
        expect(content, `${language}/${slug} explains stale data`).toContain(language === "en" ? "stale" : "desatualiz");
        expect(content, `${language}/${slug} explains freshness`).toContain(language === "en" ? "updated" : "atualizado");
      }
    }
  });
});
