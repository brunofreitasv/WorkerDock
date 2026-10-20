import type { ReactElement } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { helpTopics } from "./topics";

const tenantRoutes = new Set(["#/dashboard", "#/monitoring", "#/workers", "#/audit", "#/alerts", "#/templates", "#/jobs", "#/settings", "#/profiles"]);
const managerRoutes = new Set(["#/jobs", "#/settings", "#/profiles", "#/alerts"]);

export function HelpMarkdown({ source, canManage, onNavigate }: { source: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
  const safeSource = source.replace(/<[^>]*>/g, tag => tag.replace(/</g, "&lt;").replace(/>/g, "&gt;"));
  return <div className="help-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml components={{
    a: ({ href, children }) => {
      const topicMatch = href?.match(/^#\/help:([a-z0-9-]+)$/);
      const internalRoute = href && tenantRoutes.has(href) ? href : undefined;
      const topicRoute = topicMatch && helpTopics.some(topic => topic.slug === topicMatch[1]) ? href : undefined;
      const route = internalRoute ?? topicRoute;
      const denied = !!route && managerRoutes.has(route) && !canManage;
      if (!route || denied) return <span>{children}</span>;
      return <a href={route} onClick={event => { event.preventDefault(); onNavigate(route); }}>{children}</a>;
    },
  }}>{safeSource}</ReactMarkdown></div>;
}
