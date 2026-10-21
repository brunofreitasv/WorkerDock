import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";
import { helpTopics, type HelpTopic } from "./topics";

export type SearchResult = {
  topic: HelpTopic;
  excerpt: string;
  matchField: "title" | "keywords" | "summary" | "body" | "category";
};

const weights: Record<SearchResult["matchField"], number> = {
  title: 5,
  keywords: 4,
  summary: 3,
  body: 1,
  category: 0.5,
};

function normalize(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase();
}

function plainText(markdown: string): string {
  return markdown
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/^\s{0,3}(?:#{1,6}\s*|>\s*|[-+*]\s+|\d+[.)]\s+)/gm, "")
    .replace(/`{1,3}([^`]+)`{1,3}/g, "$1")
    .replace(/<[^>]*>/g, "")
    .replace(/[*_~]/g, "")
    .replace(/\s+/g, " ")
    .trim();
}

function excerptAround(text: string, queryTerms: string[]): string {
  if (text.length <= 160) return text;
  const normalized = normalize(text);
  const matches = queryTerms
    .map((term) => normalized.indexOf(term))
    .filter((index) => index >= 0)
    .sort((a, b) => a - b);
  const matchAt = matches[0] ?? 0;
  const start = Math.max(0, Math.min(matchAt - 65, text.length - 157));
  const end = Math.min(text.length, start + 157);
  return `${start > 0 ? "…" : ""}${text.slice(start, end).trim()}${end < text.length ? "…" : ""}`;
}

export function searchHelpTopics(
  language: Language,
  query: string,
): SearchResult[] {
  const terms = normalize(query.trim()).split(/\s+/).filter(Boolean);
  if (!terms.length) return [];

  const ranked = helpTopics.flatMap((topic, index) => {
    const body = plainText(getHelpArticle(language, topic.slug) ?? "");
    const fields: Record<SearchResult["matchField"], string> = {
      title: normalize(topic.title[language]),
      keywords: normalize(topic.keywords[language].join(" ")),
      summary: normalize(topic.summary[language]),
      body: normalize(body),
      category: normalize(topic.category[language]),
    };
    const termFields = terms.map((term) =>
      (Object.keys(weights) as SearchResult["matchField"][])
        .filter((field) => fields[field].includes(term))
        .sort((a, b) => weights[b] - weights[a]),
    );
    if (termFields.some((matches) => matches.length === 0)) return [];

    const score = termFields.reduce(
      (total, matches) => total + weights[matches[0]],
      0,
    );
    const matchField = termFields
      .flat()
      .sort((a, b) => weights[b] - weights[a])[0];
    const bodyTerms = terms.filter((term) => fields.body.includes(term));
    const excerpt = bodyTerms.length
      ? excerptAround(body, bodyTerms)
      : excerptAround(plainText(topic.summary[language]), terms);
    return [{ result: { topic, excerpt, matchField }, score, index }];
  });

  return ranked
    .sort((a, b) => b.score - a.score || a.index - b.index)
    .map((item) => item.result);
}
