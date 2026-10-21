import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DocumentationArticle } from "./DocumentationArticle";
import { helpTopics } from "./topics";

const topic = helpTopics.find(item => item.slug === "sql-jobs")!;
const source = "## Choose a profile\n\nSelect the correct profile.\n\n### Check access\n\nConfirm permissions.\n\n## Verify the run\n\nOpen the execution record.";

describe("DocumentationArticle", () => {
  it("provides breadcrumbs, an active sibling rail, an article outline, and related guides", () => {
    render(<DocumentationArticle language="en" topic={topic} source={source} canManage onNavigate={vi.fn()} />);
    expect(screen.getByRole("navigation", { name: "Breadcrumb" })).toBeTruthy();
    expect(within(screen.getByRole("navigation", { name: "Breadcrumb" })).getByRole("link", { name: "Documentation" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Build and run jobs" })).toBeTruthy();
    expect(screen.getByRole("navigation", { name: "Browse topics" })).toBeTruthy();
    expect(screen.getAllByRole("link", { name: "Configure SQL Server jobs" }).find(link => link.getAttribute("aria-current") === "page")).toBeTruthy();
    expect(screen.getAllByRole("navigation", { name: "On this page" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Choose a profile" }).every(link => link.getAttribute("href") === "#choose-a-profile")).toBe(true);
    expect(screen.getAllByRole("link", { name: "Check access" }).every(link => link.getAttribute("href") === "#check-access")).toBe(true);
    expect(screen.getByRole("button", { name: /Manage SQL Server profiles/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Next guide.*Configure HTTP jobs/ })).toBeTruthy();
  });

  it("offers an Open screen action only when the user has the required role", () => {
    const navigate = vi.fn();
    const { rerender } = render(<DocumentationArticle language="en" topic={topic} source={source} canManage={false} onNavigate={navigate} />);
    expect(screen.queryByRole("button", { name: "Open Configure SQL Server jobs" })).toBeNull();
    rerender(<DocumentationArticle language="en" topic={topic} source={source} canManage onNavigate={navigate} />);
    screen.getByRole("button", { name: "Open Configure SQL Server jobs" }).click();
    expect(navigate).toHaveBeenCalledWith("#/jobs");
  });

  it("uses responsive disclosures for the topic rail and outline", () => {
    render(<DocumentationArticle language="en" topic={topic} source={source} canManage onNavigate={vi.fn()} />);
    expect(screen.getByText("Browse topics")).toBeTruthy();
    expect(screen.getAllByText("On this page").length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: "All help topics" })).toBeNull();
  });

  it("localizes the article outline label in Brazilian Portuguese", () => {
    render(<DocumentationArticle language="pt-BR" topic={topic} source={source} canManage onNavigate={vi.fn()} />);
    expect(screen.getAllByRole("navigation", { name: "Nesta página" }).length).toBeGreaterThan(0);
  });

  it("opens the mobile topic rail from the group breadcrumb at a narrow viewport", () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 375 });
    render(<DocumentationArticle language="en" topic={topic} source={source} canManage onNavigate={vi.fn()} />);
    const breadcrumb = screen.getByRole("link", { name: "Build and run jobs" });
    expect(breadcrumb.getAttribute("href")).toBe("#topic-rail");
    fireEvent.click(breadcrumb);
    expect(document.getElementById("mobile-topic-rail")?.hasAttribute("open")).toBe(true);
  });
});
