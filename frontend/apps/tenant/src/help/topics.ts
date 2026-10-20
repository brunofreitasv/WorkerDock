import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";

export type HelpTopic = {
  slug: string;
  title: Record<Language, string>;
  category: Record<Language, string>;
  route: string;
  requiredRole: "Read" | "Manage";
};

export const helpTopics: readonly HelpTopic[] = [
  { slug: "getting-started", title: { en: "Getting started", "pt-BR": "Primeiros passos" }, category: { en: "Basics", "pt-BR": "Noções básicas" }, route: "#/dashboard", requiredRole: "Read" },
  { slug: "dashboard", title: { en: "Understand the dashboard", "pt-BR": "Entenda o painel" }, category: { en: "Monitoring", "pt-BR": "Monitoramento" }, route: "#/dashboard", requiredRole: "Read" },
];

function normalize(value: string): string {
  return value.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLocaleLowerCase();
}

export function searchHelpTopics(language: Language, query: string): HelpTopic[] {
  const needle = normalize(query.trim());
  if (!needle) return [...helpTopics];
  return helpTopics.filter(topic => {
    const body = getHelpArticle(language, topic.slug) ?? "";
    return normalize(`${topic.title[language]} ${topic.category[language]} ${body}`).includes(needle);
  });
}
