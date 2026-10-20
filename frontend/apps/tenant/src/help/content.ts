import type { Language } from "@workerdock/ui";

const bundledArticles = import.meta.glob("./content/**/*.md", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

export function getHelpArticle(language: Language, slug: string): string | undefined {
  return bundledArticles[`./content/${language}/${slug}.md`];
}
