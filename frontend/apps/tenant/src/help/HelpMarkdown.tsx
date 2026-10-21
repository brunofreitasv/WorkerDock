import type { ReactElement } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type { Language } from "@workerdock/ui";
import { helpTopics } from "./topics";
import { parseArticleHeadings, type ArticleHeading } from "./articleOutline";
import { getHelpMedia } from "./media";

const tenantRoutes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings", "#/profiles"]);
const managerRoutes = new Set(["#/jobs", "#/settings", "#/profiles"]);

export function HelpMarkdown({ source, headings = parseArticleHeadings(source), canManage, onNavigate, language = "en" }: { source: string; headings?: ArticleHeading[]; canManage: boolean; onNavigate: (route: string) => void; language?: Language }): ReactElement {
  const safeSource = source.replace(/<[^>]*>/g, tag => tag.replace(/</g, "&lt;").replace(/>/g, "&gt;"));
  let headingIndex = 0;
  return <div className="help-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml components={{
    h2: ({ children }) => <h2 id={headings[headingIndex++]?.id}>{children}</h2>,
    h3: ({ children }) => <h3 id={headings[headingIndex++]?.id}>{children}</h3>,
    a: ({ href, children }) => {
      const topicMatch = href?.match(/^#\/help:([a-z0-9-]+)$/);
      const internalRoute = href && tenantRoutes.has(href) ? href : undefined;
      const topicRoute = topicMatch && helpTopics.some(topic => topic.slug === topicMatch[1]) ? href : undefined;
      const route = internalRoute ?? topicRoute;
      const denied = !!route && managerRoutes.has(route) && !canManage;
      if (!route || denied) return <span>{children}</span>;
      return <a href={route} onClick={event => { event.preventDefault(); onNavigate(route); }}>{children}</a>;
    },
    img: ({ src, alt }) => {
      const safeSrc = src ? getHelpMedia(src) : undefined;
      const mobileSrc = src?.endsWith(".svg") ? getHelpMedia(src.replace(/\.svg$/i, "-mobile.svg")) : undefined;
      if (!safeSrc) return <span>{alt}</span>;
      const isDiagram = !!mobileSrc;
      const fullSizeLabel = language === "pt-BR" ? "Ver diagrama em tamanho original" : "View full-size diagram";
      const scrollRegionLabel = language === "pt-BR" ? "Diagrama em tamanho original, use rolagem horizontal se necessário" : "Full-size diagram, scroll horizontally if needed";
      return <>
        <picture>
          {mobileSrc && <source media="(max-width: 600px)" srcSet={mobileSrc} />}
          <img src={safeSrc} alt={alt ?? ""} loading="lazy" style={{ display: "block", width: "100%", maxWidth: "100%", height: "auto", marginInline: "auto" }} />
        </picture>
        {isDiagram && <details><summary>{fullSizeLabel}</summary><div role="region" aria-label={scrollRegionLabel} tabIndex={0} style={{ maxWidth: "100%", overflowX: "auto" }}><img src={safeSrc} alt={alt ?? ""} loading="lazy" style={{ display: "block", width: "820px", maxWidth: "none", height: "auto" }} /></div></details>}
      </>;
    },
  }}>{safeSource}</ReactMarkdown></div>;
}
