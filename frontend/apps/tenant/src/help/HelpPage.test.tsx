import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { HelpPage } from "./HelpPage";
import { helpTopics } from "./topics";

const navigate = vi.fn();

describe("tenant Documentation home", () => {
  it.each([
    ["en", "Documentation", "Run your first job", "Monitor executions", "Configure a SQL Server job", "Create an alert rule"],
    ["pt-BR", "Documentação", "Execute sua primeira tarefa", "Monitore execuções", "Configure uma tarefa do SQL Server", "Crie uma regra de alerta"],
  ] as const)("shows localized task shortcuts and grouped topics in %s", (language, title, ...shortcuts) => {
    render(<HelpPage language={language} canManage onNavigate={navigate} />);
    expect(screen.getByRole("heading", { name: title, level: 1 })).toBeTruthy();
    for (const shortcut of shortcuts) expect(screen.getAllByRole("link", { name: new RegExp(shortcut, "i") }).length).toBeGreaterThan(0);
    const groupSections = [...document.querySelectorAll('section[aria-labelledby^="documentation-group-"]')];
    expect(groupSections.map(section => section.querySelector("h2")?.textContent)).toEqual([
      language === "en" ? "Start here" : "Comece aqui",
      language === "en" ? "Operate and monitor" : "Operação e monitoramento",
      language === "en" ? "Build and run jobs" : "Criação e execução de tarefas",
      language === "en" ? "Integrations and alerts" : "Integrações e alertas",
      language === "en" ? "Administration" : "Administração",
    ]);
    expect(groupSections.map(section => section.querySelectorAll('a[href^="#/help:"]').length)).toEqual([2, 5, 8, 6, 3]);
  });

  it("has an accessible prominent search with ranked excerpts and a clear action", () => {
    render(<HelpPage language="en" canManage onNavigate={navigate} />);
    const search = screen.getByRole("searchbox", { name: "Search documentation" });
    fireEvent.change(search, { target: { value: "SQL Server job" } });
    expect(screen.getByRole("button", { name: "Clear search" })).toBeTruthy();
    const results = screen.getByRole("region", { name: "Search results" });
    expect(within(results).getAllByRole("link")[0].textContent).toMatch(/SQL Server job/i);
    const sqlSummary = helpTopics.find(topic => topic.slug === "sql-jobs")!.summary.en;
    expect(within(results).getByText((_content, element) => element?.tagName === "P" && element.textContent === sqlSummary)).toBeTruthy();
    expect(within(results).getAllByText(/Create|configure|SQL Server/i).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));
    expect(search).toHaveProperty("value", "");
  });

  it("indexes and safely highlights the topic group in search results", () => {
    const { container } = render(<HelpPage language="en" canManage onNavigate={navigate} />);
    fireEvent.change(screen.getByRole("searchbox", { name: "Search documentation" }), { target: { value: "operate" } });
    const results = screen.getByRole("region", { name: "Search results" });
    expect(within(results).getAllByRole("link")[0].textContent).toBe("Understand the dashboard");
    expect(within(results).getAllByText("Operate", { selector: "mark" }).length).toBeGreaterThan(0);
    expect(container.querySelector("script, img[onerror]")).toBeNull();
  });

  it.each([["en", "No documentation matched your search."], ["pt-BR", "Nenhum documento corresponde à sua pesquisa."]] as const)("offers a useful empty search state in %s", (language, message) => {
    render(<HelpPage language={language} canManage onNavigate={navigate} />);
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "xyz-no-such-topic" } });
    expect(screen.getByText(message)).toBeTruthy();
    expect(screen.getByRole("button", { name: /clear|limpar/i })).toBeTruthy();
  });

  it("marks Manage guides in the topic list", () => {
    render(<HelpPage language="en" canManage={false} onNavigate={navigate} />);
    const managerTopics = helpTopics.filter(topic => topic.requiredRole === "Manage");
    expect(managerTopics.length).toBeGreaterThan(0);
    for (const topic of managerTopics) {
      const link = screen.getAllByRole("link", { name: new RegExp(topic.title.en, "i") }).find(item => item.getAttribute("href") === `#/help:${topic.slug}` && item.closest("article"))!;
      expect(link.closest("article")?.textContent).toMatch(/Manage/);
    }
  });

  it.each([["en", "Unknown documentation topic", "Search documentation"], ["pt-BR", "Tópico de documentação não encontrado", "Pesquisar documentação"]] as const)("shows localized grouped recovery and search for an invalid topic in %s", (language, missing, search) => {
    render(<HelpPage language={language} slug="bad:slug" canManage={false} onNavigate={navigate} />);
    expect(screen.getByText(missing)).toBeTruthy();
    expect(screen.getByRole("searchbox", { name: search })).toBeTruthy();
    expect(screen.getAllByRole("link", { name: /Get oriented|Conheça o WorkerDock/ }).length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: /All help topics|Todos os tópicos de ajuda/i })).toBeNull();
  });

  it.each([["workers-audit", "Check worker health"], ["alerts", "Create alert rules"]] as const)("resolves legacy alias %s to canonical article %s", (slug, title) => {
    render(<HelpPage language="en" slug={slug} canManage onNavigate={navigate} />);
    expect(screen.getByRole("heading", { name: title, level: 1 })).toBeTruthy();
    expect(screen.getByRole("navigation", { name: "Breadcrumb" })).toBeTruthy();
  });
});
