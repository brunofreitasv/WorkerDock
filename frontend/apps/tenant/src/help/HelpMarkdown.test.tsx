import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { parseArticleHeadings } from "./articleOutline";
import { HelpMarkdown } from "./HelpMarkdown";

describe("HelpMarkdown", () => {
  it("uses the parsed outline heading IDs in the article", () => {
    const source = "## Configuração\n### Steps\n## Steps";
    const headings = parseArticleHeadings(source);
    const { container } = render(<HelpMarkdown source={source} headings={headings} canManage onNavigate={vi.fn()} />);
    expect([...container.querySelectorAll("h2, h3")].map(item => item.id)).toEqual(headings.map(item => item.id));
  });

  it("keeps IDs aligned for setext and indented headings rendered by ReactMarkdown", () => {
    const source = "Setext heading\n---\n ## Indented two\n   ### Indented three";
    const headings = parseArticleHeadings(source);
    const { container } = render(<HelpMarkdown source={source} headings={headings} canManage onNavigate={vi.fn()} />);
    expect([...container.querySelectorAll("h2, h3")].map(item => [item.tagName, item.id])).toEqual([
      ["H2", "setext-heading"],
      ["H2", "indented-two"],
      ["H3", "indented-three"],
    ]);
  });

  it("renders raw HTML as text", () => {
    const { container } = render(<HelpMarkdown source={'<img src=x onerror="alert(1)">'} headings={[]} canManage onNavigate={vi.fn()} />);
    expect(container.querySelector("img")).toBeNull();
    expect(container.textContent).toContain("<img");
  });

  it.each(["javascript:alert(1)", "#/not-listed"])("keeps disallowed link %s inert", href => {
    const navigate = vi.fn();
    render(<HelpMarkdown source={`[unsafe](${href})`} headings={[]} canManage onNavigate={navigate} />);
    const link = screen.queryByRole("link", { name: "unsafe" });
    if (link) fireEvent.click(link);
    expect(navigate).not.toHaveBeenCalled();
  });

  it("renders unknown and remote images as alt text without image requests", () => {
    const { container } = render(<HelpMarkdown source={'![local](./media/missing.svg) ![remote](https://example.invalid/image.png)'} headings={[]} canManage onNavigate={vi.fn()} />);
    expect(container.querySelectorAll("img")).toHaveLength(0);
    expect(container.textContent).toContain("local");
    expect(container.textContent).toContain("remote");
  });

  it("scales bundled documentation diagrams to the article width", () => {
    const { container } = render(<HelpMarkdown source={'![workflow](./media/first-job.svg)'} headings={[]} canManage onNavigate={vi.fn()} />);
    const image = container.querySelector("img");
    expect(image).not.toBeNull();
    expect(image).toHaveStyle({ width: "100%", maxWidth: "100%", height: "auto" });
  });

  it("allows readers to follow alerts but blocks manager routes", () => {
    const navigate = vi.fn();
    render(<HelpMarkdown source="[Alerts](#/alerts) [Jobs](#/jobs) [Settings](#/settings)" headings={[]} canManage={false} onNavigate={navigate} />);
    fireEvent.click(screen.getByRole("link", { name: "Alerts" }));
    expect(navigate).toHaveBeenCalledWith("#/alerts");
    expect(screen.queryByRole("link", { name: "Jobs" })).toBeNull();
    expect(screen.queryByRole("link", { name: "Settings" })).toBeNull();
  });
});
