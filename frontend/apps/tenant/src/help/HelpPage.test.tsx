import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { getHelpArticle } from "./content";
import { HelpMarkdown } from "./HelpMarkdown";
import { HelpPage } from "./HelpPage";
import { helpTopics, searchHelpTopics } from "./topics";

const navigate = vi.fn();

describe("tenant help guides", () => {
  it.each(["en", "pt-BR"] as const)("shows starter topics in %s", language => {
    render(<HelpPage language={language} canManage onNavigate={navigate} />);
    expect(screen.getByText(helpTopics.find(topic => topic.slug === "getting-started")!.title[language])).toBeTruthy();
    expect(screen.getByText(helpTopics.find(topic => topic.slug === "dashboard")!.title[language])).toBeTruthy();
  });

  it("bundles both starter articles", () => {
    for (const language of ["en", "pt-BR"] as const) {
      expect(getHelpArticle(language, "getting-started")).toContain("##");
      expect(getHelpArticle(language, "dashboard")).toContain("##");
    }
  });

  it.each(["en", "pt-BR"] as const)("bundles monitoring, alert, profile, and template guides in %s", language => {
    const expected = ["monitoring", "workers-audit", "alerts", "sql-profiles", "templates"];
    const routes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings"]);
    for (const slug of expected) {
      const topic = helpTopics.find(item => item.slug === slug);
      const article = getHelpArticle(language, slug);
      expect(topic, `${language} manifest entry for ${slug}`).toBeTruthy();
      expect(article, `${language} article for ${slug}`).toBeTruthy();
      const sections = language === "en" ? ["Prerequisites", "Role", "Steps", "Result"] : ["Pré-requisitos", "Permissão", "Etapas", "Resultado"];
      for (const section of sections) expect(article).toMatch(new RegExp(`^## ${section}$`, "m"));
      expect(topic?.route).toMatch(/^#\/[a-z-]+$/);
      expect(routes.has(topic?.route ?? "")).toBe(true);
      for (const [, route] of article!.matchAll(/\]\((#\/[^)]+)\)/g)) {
        const helpSlug = route.match(/^#\/help:([a-z0-9-]+)$/)?.[1];
        expect(routes.has(route) || helpTopics.some(item => item.slug === helpSlug), `${slug} route ${route}`).toBe(true);
      }
    }
  });

  it("documents current monitoring, alert setup, SQL diagnostic, and developer template workflows", () => {
    const article = (slug: string) => getHelpArticle("en", slug) ?? "";
    expect(article("alerts")).toMatch(/sink[\s\S]*disabled[\s\S]*enable/i);
    expect(article("alerts")).toMatch(/outbound policy/i);
    expect(article("alerts")).toMatch(/secret reference/i);
    expect(article("alerts")).toMatch(/incident[\s\S]*deliver/i);
    expect(article("sql-profiles")).toMatch(/Run safe diagnostic/i);
    expect(article("monitoring")).toMatch(/timeline/i);
    expect(article("monitoring")).toMatch(/stale data/i);
    expect(article("monitoring")).toMatch(/memory/i);
    expect(article("workers-audit")).toMatch(/worker/i);
    expect(article("workers-audit")).toMatch(/audit/i);
    expect(article("templates")).toContain("docs/how-to/add-custom-template.md");
    expect(helpTopics.find(topic => topic.slug === "monitoring")?.route).toBe("#/monitoring");
    expect(helpTopics.find(topic => topic.slug === "sql-profiles")?.route).toBe("#/settings");
  });

  it.each([
    ["en", "Unknown help topic", "All help topics"],
    ["pt-BR", "Tópico de ajuda não encontrado", "Todos os tópicos"],
  ] as const)("shows localized recovery for an invalid topic in %s", (language, missing, index) => {
    render(<HelpPage language={language} slug="bad:slug" canManage={false} onNavigate={navigate} />);
    expect(screen.getByText(missing)).toBeTruthy();
    expect(screen.getByRole("button", { name: index })).toBeTruthy();
  });

  it("shows manager guidance to readers while blocking manager destinations", () => {
    render(<HelpPage language="en" slug="getting-started" canManage={false} onNavigate={navigate} />);
    expect(screen.getAllByText(/Manage/).length).toBeGreaterThan(0);
    expect(screen.queryByRole("link", { name: /jobs/i })).toBeNull();
    expect(screen.queryByRole("link", { name: /settings/i })).toBeNull();
  });

  it("searches titles and article bodies without case or accent sensitivity", () => {
    expect(searchHelpTopics("en", "DASHBOARD").map(topic => topic.slug)).toContain("dashboard");
    expect(searchHelpTopics("en", "capacity").map(topic => topic.slug)).toContain("dashboard");
    expect(searchHelpTopics("pt-BR", "proximos").map(topic => topic.slug)).toContain("dashboard");
  });

  it("renders raw HTML as text without creating elements", () => {
    const { container } = render(<HelpMarkdown source={'<img src=x onerror="alert(1)">'} canManage onNavigate={navigate} />);
    expect(container.querySelector("img")).toBeNull();
    expect(container.textContent).toContain("<img");
  });

  it.each(["javascript:alert(1)", "#/not-listed"])("does not navigate disallowed link %s", href => {
    render(<HelpMarkdown source={`[unsafe](${href})`} canManage onNavigate={navigate} />);
    const link = screen.queryByRole("link", { name: "unsafe" });
    if (link) fireEvent.click(link);
    expect(navigate).not.toHaveBeenCalled();
  });
});
