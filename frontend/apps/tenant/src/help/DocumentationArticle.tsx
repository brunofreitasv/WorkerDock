import { useMemo, useState, type ReactElement } from "react";
import { Box, Breadcrumbs, Button, Chip, Link, Paper, Stack, Typography } from "@mui/material";
import type { Language } from "@workerdock/ui";
import { helpGroups, helpTopics, type HelpTopic } from "./topics";
import { parseArticleHeadings } from "./articleOutline";
import { HelpMarkdown } from "./HelpMarkdown";
import { documentationCopy } from "./documentationCopy";

function scrollBehavior(): ScrollBehavior {
  return window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
}

export function DocumentationArticle({ language, topic, source, canManage, onNavigate }: { language: Language; topic: HelpTopic; source: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
  const copy = documentationCopy[language];
  const group = helpGroups.find(item => item.id === topic.group)!;
  const [mobileTopicsOpen, setMobileTopicsOpen] = useState(false);
  const siblings = helpTopics.filter(item => item.group === topic.group);
  const headings = useMemo(() => parseArticleHeadings(source), [source]);
  const topicIndex = helpTopics.findIndex(item => item.slug === topic.slug);
  const nextTopic = helpTopics[topicIndex + 1];
  const groupNext = nextTopic;
  const related = topic.related.map(slug => helpTopics.find(item => item.slug === slug)).filter((item): item is HelpTopic => !!item).slice(0, 3);
  const mayOpen = topic.requiredRole === "Read" || canManage;
  const browseLabel = language === "en" ? "Browse topics" : "Explorar tópicos";
  const outlineLabel = language === "en" ? "On this page" : "Nesta página";
  const nextLabel = language === "en" ? "Next guide" : "Próximo guia";
  const openLabel = language === "en" ? `Open ${topic.title[language]}` : `Abrir ${topic.title[language]}`;
  const topicLink = (item: HelpTopic, active = false) => <Link key={item.slug} href={`#/help:${item.slug}`} aria-current={active ? "page" : undefined} onClick={event => { event.preventDefault(); onNavigate(`help:${item.slug}`); }} underline={active ? "none" : "hover"} color={active ? "text.primary" : "inherit"} sx={{ display: "block", borderRadius: 1, px: 1.25, py: 0.85, fontWeight: active ? 700 : 500, bgcolor: active ? "action.selected" : "transparent", textDecoration: "none", "&:focus-visible": { outline: "2px solid", outlineColor: "primary.main" } }}>{item.title[language]}</Link>;
  const topicNav = (label: string) => <Box component="nav" aria-label={label}><Typography variant="overline" color="text.secondary" sx={{ px: 1.25 }}>{group.title[language]}</Typography><Stack spacing={0.25}>{siblings.map(item => topicLink(item, item.slug === topic.slug))}</Stack></Box>;
  const outlineNav = (label = outlineLabel) => <Box component="nav" aria-label={label}><Typography variant="subtitle2" sx={{ mb: 1 }}>{label}</Typography><Stack spacing={0.5}>{headings.map(heading => <Link key={heading.id} href={`#${heading.id}`} onClick={event => { event.preventDefault(); document.getElementById(heading.id)?.scrollIntoView({ behavior: scrollBehavior(), block: "start" }); }} color="text.secondary" underline="hover" sx={{ pl: heading.level === 3 ? 1.5 : 0, fontSize: "0.875rem" }}>{heading.title}</Link>)}</Stack></Box>;

  return <Box component="main" sx={{ width: "100%", maxWidth: 1440, mx: "auto", px: { xs: 2, sm: 3, lg: 4 }, pb: 6 }}>
    <Breadcrumbs aria-label="Breadcrumb" sx={{ mb: 2.5 }}>
      <Link href="#/help" onClick={event => { event.preventDefault(); onNavigate("#/help"); }} underline="hover" color="inherit">{copy.title}</Link>
      <Link href="#topic-rail" underline="hover" color="inherit" onClick={event => { event.preventDefault(); if ((window.matchMedia?.("(min-width:1200px)").matches ?? window.innerWidth >= 1200)) { document.getElementById("topic-rail")?.scrollIntoView({ behavior: scrollBehavior(), block: "start" }); } else { setMobileTopicsOpen(true); requestAnimationFrame(() => document.getElementById("mobile-topic-rail")?.scrollIntoView({ behavior: scrollBehavior(), block: "start" })); } }}>{group.title[language]}</Link>
      <Typography color="text.primary" aria-current="page">{topic.title[language]}</Typography>
    </Breadcrumbs>
    <Box sx={{ display: { xs: "flex", lg: "none" }, flexDirection: "column", gap: 1.25, mb: 2 }}>
      <details id="mobile-topic-rail" open={mobileTopicsOpen} onToggle={event => setMobileTopicsOpen(event.currentTarget.open)}><summary style={{ cursor: "pointer", fontWeight: 600 }}>{browseLabel}</summary><Box sx={{ pt: 1 }}>{topicNav(browseLabel)}</Box></details>
      <details><summary style={{ cursor: "pointer", fontWeight: 600 }}>{outlineLabel}</summary><Box sx={{ pt: 1 }}>{outlineNav()}</Box></details>
    </Box>
    <Box sx={{ display: "grid", gridTemplateColumns: { xs: "minmax(0, 1fr)", lg: "220px minmax(0, 1fr) 200px" }, gap: { xs: 2, lg: 4 }, alignItems: "start" }}>
      <Box id="topic-rail" sx={{ display: { xs: "none", lg: "block" }, position: "sticky", top: 24, maxHeight: "calc(100vh - 48px)", overflowY: "auto" }}>{topicNav("Topics")}</Box>
      <Box component="article" sx={{ minWidth: 0, maxWidth: 820, width: "100%", mx: "auto" }}>
        <Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: "wrap" }}><Chip size="small" label={group.title[language]} variant="outlined" /><Chip size="small" label={topic.requiredRole === "Manage" ? copy.manage : copy.read} variant="outlined" /></Stack>
        <Typography component="h1" variant="h3" sx={{ fontSize: { xs: "2rem", sm: "2.5rem" }, fontWeight: 750, letterSpacing: "-.03em", mb: 2 }}>{topic.title[language]}</Typography>
        <Typography color="text.secondary" sx={{ fontSize: "1.1rem", lineHeight: 1.7, mb: 2.5 }}>{topic.summary[language]}</Typography>
        {mayOpen && <Button variant="contained" onClick={() => onNavigate(topic.route)} sx={{ mb: 3 }}>{openLabel}</Button>}
        <Paper variant="outlined" sx={{ p: { xs: 2, sm: 3 }, borderRadius: 2 }}><HelpMarkdown source={source.replace(/^# .+\r?\n+/, "")} headings={headings} canManage={canManage} onNavigate={onNavigate} language={language} /></Paper>
        <Stack spacing={1.5} sx={{ mt: 3 }}>
          {related.length > 0 && <Box component="section" aria-labelledby="related-guides-heading"><Typography id="related-guides-heading" component="h2" variant="h6" sx={{ mb: 1 }}>{language === "en" ? "Related guides" : "Guias relacionados"}</Typography><Stack direction="row" sx={{ flexWrap: "wrap", gap: 1 }}>{related.map(item => <Button key={item.slug} variant="outlined" size="small" onClick={() => onNavigate(`help:${item.slug}`)}>{item.title[language]}</Button>)}</Stack></Box>}
          {groupNext && <Paper component="button" type="button" onClick={() => onNavigate(`help:${groupNext.slug}`)} variant="outlined" sx={{ p: 2, textAlign: "left", cursor: "pointer", color: "inherit", bgcolor: "background.paper", "&:hover": { borderColor: "primary.main", bgcolor: "action.hover" } }}><Typography variant="caption" color="text.secondary">{nextLabel}</Typography><Typography variant="subtitle1" sx={{ fontWeight: 650 }}>{groupNext.title[language]} →</Typography></Paper>}
        </Stack>
      </Box>
      <Box sx={{ display: { xs: "none", lg: "block" }, position: "sticky", top: 24, maxHeight: "calc(100vh - 48px)", overflowY: "auto" }}>{outlineNav()}</Box>
    </Box>
  </Box>;
}
