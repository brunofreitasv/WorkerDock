import { describe, expect, it } from "vitest";
import { searchHelpTopics } from "./search";
import { helpTopics } from "./topics";

describe("documentation search", () => {
  it("requires all terms even when they match the title and body separately", () => {
    const topic = helpTopics.find((item) => item.slug === "dashboard")!;
    const metadata = [
      topic.title.en,
      ...topic.keywords.en,
      topic.summary.en,
    ].join(" ").toLowerCase();
    expect(metadata).not.toContain("timestamp");
    expect(
      searchHelpTopics("en", "dashboard timestamp").map(
        (result) => result.topic.slug,
      ),
    ).toContain("dashboard");
    expect(searchHelpTopics("en", "dashboard missingword")).toEqual([]);
  });

  it("does not search group labels as a match field", () => {
    expect(searchHelpTopics("en", "operate")).toEqual([]);
  });

  it("matches Portuguese terms without accents", () => {
    expect(
      searchHelpTopics("pt-BR", "proximos").map((result) => result.topic.slug),
    ).toContain("dashboard");
  });

  it("ranks title matches ahead of body matches", () => {
    const results = searchHelpTopics("en", "dashboard");
    expect(results[0]?.topic.slug).toBe("dashboard");
    expect(results[0]?.matchField).toBe("title");
  });

  it("returns a plain text excerpt without Markdown syntax", () => {
    const result = searchHelpTopics("en", "capacity").find(
      (item) => item.topic.slug === "dashboard",
    );
    expect(result?.excerpt).toContain("capacity");
    expect(result?.excerpt.length).toBeLessThanOrEqual(160);
    expect(result?.excerpt).not.toMatch(/[#*`\[\]]/);
  });

  it("returns no results for an empty query", () => {
    expect(searchHelpTopics("en", "   ")).toEqual([]);
  });
});
