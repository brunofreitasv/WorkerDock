import type { ReactElement } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { helpTopics } from "./topics";
import { parseArticleHeadings, type ArticleHeading } from "./articleOutline";
import { getHelpMedia } from "./media";

const tenantRoutes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings", "#/profiles"]);
const managerRoutes = new Set(["#/jobs", "#/settings", "#/profiles"]);

export function HelpMarkdown({ source, headings = parseArticleHeadings(source), canManage, onNavigate }: { source: string; headings?: ArticleHeading[]; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
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
      return safeSrc ? <img src={safeSrc} alt={alt ?? ""} loading="lazy" /> : <span>{alt}</span>;
    },
  }}>{safeSource}</ReactMarkdown></div>;
}
