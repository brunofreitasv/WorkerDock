import type { ReactElement } from "react";
import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";
import { HelpMarkdown } from "./HelpMarkdown";
import { resolveHelpTopic } from "./topics";
import { DocumentationHome } from "./DocumentationHome";
import { documentationCopy } from "./documentationCopy";

export function HelpPage({ language, slug, canManage, onNavigate }: { language: Language; slug?: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
  const labels = documentationCopy[language];
  if (slug === undefined) return <DocumentationHome language={language} canManage={canManage} onNavigate={onNavigate} />;

  const topic = resolveHelpTopic(slug);
  const article = topic ? getHelpArticle(language, topic.slug) : undefined;
  if (article && topic) return <main>
    <button type="button" onClick={() => onNavigate("#/help")}>{labels.back}</button>
    <h1>{topic.title[language]}</h1>
    <p>{language === "en" ? "Required role" : "Função necessária"}: {topic.requiredRole === "Manage" ? labels.manage : labels.read}</p>
    <HelpMarkdown source={article} canManage={canManage} onNavigate={onNavigate} />
  </main>;

  return <main>
    <h1>{labels.title}</h1>
    <p role="alert">{labels.notFound}</p>
    <button type="button" onClick={() => onNavigate("#/help")}>{labels.back}</button>
  </main>;
}
