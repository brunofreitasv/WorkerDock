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
