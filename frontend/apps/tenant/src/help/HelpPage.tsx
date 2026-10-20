import { useMemo, useState, type ReactElement } from "react";
import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";
import { HelpMarkdown } from "./HelpMarkdown";
import { helpTopics, searchHelpTopics } from "./topics";

const copy = {
  en: { title: "Help", search: "Search help", role: "Required role", read: "Read", manage: "Manage", back: "All help topics", empty: "No matching topics." },
  "pt-BR": { title: "Ajuda", search: "Pesquisar ajuda", role: "Função necessária", read: "Leitura", manage: "Gerenciar", back: "Todos os tópicos", empty: "Nenhum tópico correspondente." },
} satisfies Record<Language, Record<string, string>>;

export function HelpPage({ language, slug, canManage, onNavigate }: { language: Language; slug?: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
  const labels = copy[language];
  const [query, setQuery] = useState("");
  const article = slug ? getHelpArticle(language, slug) : undefined;
  const topic = slug ? helpTopics.find(item => item.slug === slug) : undefined;
  const results = useMemo(() => searchHelpTopics(language, query), [language, query]);
  if (article && topic) return <main><button type="button" onClick={() => onNavigate("#/help")}>{labels.back}</button><h1>{topic.title[language]}</h1><p>{labels.role}: {topic.requiredRole === "Manage" ? labels.manage : labels.read}</p><HelpMarkdown source={article} canManage={canManage} onNavigate={onNavigate} /></main>;
  return <main><h1>{labels.title}</h1><label>{labels.search}<input type="search" value={query} onChange={event => setQuery(event.target.value)} /></label><ul>{results.map(item => <li key={item.slug}><a href={`#/help:${item.slug}`} onClick={event => { event.preventDefault(); onNavigate(`#/help:${item.slug}`); }}>{item.title[language]}</a><span> · {item.category[language]}</span></li>)}</ul>{results.length === 0 && <p>{labels.empty}</p>}</main>;
}
