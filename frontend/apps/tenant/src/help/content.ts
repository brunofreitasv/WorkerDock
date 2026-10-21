import type { Language } from "@workerdock/ui";
import { resolveHelpTopic } from "./topics";

const bundledArticles = import.meta.glob("./content/**/*.md", {
  eager: true,
  query: "?raw",
  import: "default",
}) as Record<string, string>;

const legacyArticleSlugs: Readonly<Record<string, string>> = {
  workers: "workers-audit",
  "alert-rules": "alerts",
};

export function getHelpArticle(
  language: Language,
  slug: string,
): string | undefined {
  const topic = resolveHelpTopic(slug);
  if (!topic) return undefined;
  const path = `./content/${language}`;
  const legacySlug = legacyArticleSlugs[topic.slug];
  return (
    bundledArticles[`${path}/${topic.slug}.md`] ??
    (legacySlug ? bundledArticles[`${path}/${legacySlug}.md`] : undefined)
  );
}
