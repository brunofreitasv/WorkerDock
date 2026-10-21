import { describe, expect, it } from "vitest";
import { getHelpArticle } from "./content";
import { helpGroups, helpTopics, resolveHelpTopic } from "./topics";

describe("documentation topic manifest", () => {
  it("keeps the 24 canonical topics in the five specified group orders", () => {
    expect(helpGroups.map((group) => group.id)).toEqual([
      "start-here",
      "operate-monitor",
      "build-run-jobs",
      "integrations-alerts",
      "administration",
    ]);
    expect(
      helpGroups.map((group) =>
        helpTopics
          .filter((topic) => topic.group === group.id)
          .map((topic) => topic.slug),
      ),
    ).toEqual([
      ["getting-started", "first-job"],
      ["dashboard", "monitoring", "execution-events", "workers", "audit"],
      [
        "templates",
        "jobs",
        "sql-jobs",
        "http-jobs",
        "job-inputs",
        "schedules",
        "manual-runs",
        "execution-policies",
      ],
      [
        "sql-profiles",
        "outbound-policy",
        "secrets",
        "webhook-sinks",
        "alert-rules",
        "incidents-deliveries",
      ],
      ["settings", "retention", "troubleshooting"],
    ]);
  });

  it("has unique topic IDs and complete bilingual metadata with valid related topics", () => {
    const slugs = helpTopics.map((topic) => topic.slug);
    expect(new Set(slugs).size).toBe(24);
    expect(new Set(helpGroups.map((group) => group.id)).size).toBe(5);
    for (const topic of helpTopics) {
      expect(helpGroups.some((group) => group.id === topic.group)).toBe(true);
      expect(topic.route).toMatch(/^#\/[a-z-]+$/);
      expect(["Read", "Manage"]).toContain(topic.requiredRole);
      for (const language of ["en", "pt-BR"] as const) {
        expect(topic.title[language].trim()).toBeTruthy();
        expect(topic.summary[language].trim()).toBeTruthy();
        expect(topic.keywords[language].length).toBeGreaterThan(0);
        expect(
          topic.keywords[language].every((keyword) => keyword.trim()),
        ).toBe(true);
      }
      expect(topic.related.every((slug) => slugs.includes(slug))).toBe(true);
    }
  });

  it("resolves the two published aliases to their canonical guides", () => {
    expect(resolveHelpTopic("workers-audit")?.slug).toBe("workers");
    expect(resolveHelpTopic("alerts")?.slug).toBe("alert-rules");
    for (const language of ["en", "pt-BR"] as const) {
      expect(getHelpArticle(language, "workers-audit")).toBe(
        getHelpArticle(language, "workers"),
      );
      expect(getHelpArticle(language, "alerts")).toBe(
        getHelpArticle(language, "alert-rules"),
      );
    }
  });

  it("returns undefined for missing, malformed, and unknown topic slugs", () => {
    expect(resolveHelpTopic(undefined)).toBeUndefined();
    expect(resolveHelpTopic("../jobs")).toBeUndefined();
    expect(resolveHelpTopic("Unknown Topic")).toBeUndefined();
  });
});
