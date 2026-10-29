import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";
import { getHelpMedia } from "./media";
import { helpTopics, resolveHelpTopic } from "./topics";

const routes = new Set([
  "#/dashboard", "#/monitoring", "#/executions", "#/jobs", "#/workers", "#/audit",
  "#/templates", "#/settings", "#/alerts", "#/help",
]);

describe("complete Documentation corpus", () => {
  it("contains all 24 topics in English and Brazilian Portuguese", () => {
    expect(helpTopics).toHaveLength(24);
    for (const topic of helpTopics) {
      for (const language of ["en", "pt-BR"] as const) {
        const article = getHelpArticle(language, topic.slug);
        expect(article, `${language}/${topic.slug}`).toBeTruthy();
        expect(article!.trim().length, `${language}/${topic.slug} has content`).toBeGreaterThan(500);
      }
    }
  });

  it("keeps aliases canonical and all related/internal article links resolvable", () => {
    expect(resolveHelpTopic("workers-audit")?.slug).toBe("workers");
    expect(resolveHelpTopic("alerts")?.slug).toBe("alert-rules");
    const canonical = new Set(helpTopics.map(topic => topic.slug));
    for (const topic of helpTopics) {
      for (const related of topic.related) expect(canonical, `${topic.slug} related ${related}`).toContain(related);
      for (const language of ["en", "pt-BR"] as const) {
        const article = getHelpArticle(language, topic.slug)!;
        for (const [, href] of article.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
          if (href.startsWith("#/help:")) {
            expect(canonical, `${language}/${topic.slug} -> ${href}`).toContain(href.slice("#/help:".length));
          } else if (href.startsWith("#/")) {
            expect(routes, `${language}/${topic.slug} -> ${href}`).toContain(href);
          } else if (href.startsWith("./media/")) {
            expect(getHelpMedia(href), `${language}/${topic.slug} -> ${href}`).toBeDefined();
          } else {
            throw new Error(`Unexpected documentation link ${language}/${topic.slug} -> ${href}`);
          }
        }
      }
    }
  });
});
