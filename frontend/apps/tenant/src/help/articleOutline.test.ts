import { describe, expect, it } from "vitest";
import { parseArticleHeadings } from "./articleOutline";

describe("parseArticleHeadings", () => {
  it("creates normalized IDs for accented headings", () => {
    expect(parseArticleHeadings("## Configuração do perfil")).toEqual([
      { level: 2, id: "configuracao-do-perfil", title: "Configuração do perfil" },
    ]);
  });

  it("adds stable numeric suffixes for duplicate IDs", () => {
    expect(parseArticleHeadings("## Steps\n### Steps\n## Steps").map(heading => heading.id)).toEqual([
      "steps", "steps-2", "steps-3",
    ]);
  });

  it("preserves H2 and H3 source order and ignores other heading levels", () => {
    expect(parseArticleHeadings("# Title\n## First\n### Detail\n#### Deep\n## Second").map(heading => [heading.level, heading.title])).toEqual([
      [2, "First"], [3, "Detail"], [2, "Second"],
    ]);
  });

  it.each(["```", "~~~"] as const)("ignores H2 and H3 lines inside %s fenced blocks", marker => {
    const source = `## Before\n${marker}ts\n## Hidden\n### Also hidden\n${marker}\n### After`;
    expect(parseArticleHeadings(source).map(heading => heading.title)).toEqual(["Before", "After"]);
  });

  it("does not close a fence with a different marker or a shorter run", () => {
    const source = "~~~\n## Hidden\n```\n### Still hidden\n~~\n## Still hidden\n~~~~\n## Visible";
    expect(parseArticleHeadings(source).map(heading => heading.title)).toEqual(["Visible"]);
  });

  it("recognizes setext H2 and ATX headings indented up to three spaces", () => {
    expect(parseArticleHeadings("Setext heading\n---\n ## Indented two\n   ### Indented three")).toEqual([
      { level: 2, id: "setext-heading", title: "Setext heading" },
      { level: 2, id: "indented-two", title: "Indented two" },
      { level: 3, id: "indented-three", title: "Indented three" },
    ]);
  });

  it("does not treat a fence indented by four spaces as a fenced block", () => {
    expect(parseArticleHeadings("    ```\n## Visible heading").map(heading => heading.title)).toEqual(["Visible heading"]);
  });
});
