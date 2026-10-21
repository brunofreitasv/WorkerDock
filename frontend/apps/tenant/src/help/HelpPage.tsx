import { useMemo, useState, type ReactElement } from "react";
import { Box, Link, Paper, Stack, TextField, Typography } from "@mui/material";
import type { Language } from "@workerdock/ui";
import { getHelpArticle } from "./content";
import { resolveHelpTopic, helpGroups, helpTopics } from "./topics";
import { DocumentationHome } from "./DocumentationHome";
import { DocumentationArticle } from "./DocumentationArticle";
import { documentationCopy } from "./documentationCopy";
import { searchHelpTopics } from "./search";

export function HelpPage({ language, slug, canManage, onNavigate }: { language: Language; slug?: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement {
  const labels = documentationCopy[language];
  if (slug === undefined) return <DocumentationHome language={language} canManage={canManage} onNavigate={onNavigate} />;

  const topic = resolveHelpTopic(slug);
  const article = topic ? getHelpArticle(language, topic.slug) : undefined;
  if (article && topic) return <DocumentationArticle language={language} topic={topic} source={article} canManage={canManage} onNavigate={onNavigate} />;
  return <DocumentationRecovery language={language} onNavigate={onNavigate} />;
}

function DocumentationRecovery({ language, onNavigate }: { language: Language; onNavigate: (route: string) => void }): ReactElement {
  const labels = documentationCopy[language];
  const [query, setQuery] = useState("");
  const results = useMemo(() => searchHelpTopics(language, query), [language, query]);
  const copy = language === "en" ? {
    missing: "Unknown documentation topic",
    description: "Search the guides or browse a topic group below.",
    search: "Search documentation",
    groups: ["Start here", "Operate and monitor", "Build and run jobs", "Integrations and alerts", "Administration"],
    noResults: "No documentation matched your search.",
  } : {
    missing: "Tópico de documentação não encontrado",
    description: "Pesquise os guias ou explore um grupo de tópicos abaixo.",
    search: "Pesquisar documentação",
    groups: ["Comece aqui", "Operação e monitoramento", "Criação e execução de tarefas", "Integrações e alertas", "Administração"],
    noResults: "Nenhum documento corresponde à sua pesquisa.",
  };
  const visibleTopics = query.trim() ? results.map(result => result.topic) : helpTopics;
  return <Box component="main" sx={{ width: "100%", maxWidth: 1120, mx: "auto", px: { xs: 2, sm: 3 }, pb: 5 }}>
    <Typography component="h1" variant="h4" sx={{ mb: 1 }}>{labels.title}</Typography>
    <Typography role="alert" color="text.secondary" sx={{ mb: 1 }}>{copy.missing}</Typography>
    <Typography color="text.secondary" sx={{ mb: 3 }}>{copy.description}</Typography>
    <TextField type="search" fullWidth label={copy.search} value={query} onChange={event => setQuery(event.target.value)} sx={{ mb: 3 }} />
    {query.trim() && !visibleTopics.length ? <Paper variant="outlined" sx={{ p: 3 }}><Typography>{copy.noResults}</Typography></Paper> : <Stack spacing={3}>{helpGroups.map((group, index) => {
      const topics = visibleTopics.filter(item => item.group === group.id);
      if (!topics.length) return null;
      return <Box component="section" key={group.id} aria-labelledby={`recovery-${group.id}`}>
        <Typography component="h2" id={`recovery-${group.id}`} variant="h6" sx={{ mb: 1 }}>{copy.groups[index]}</Typography>
        <Paper variant="outlined" sx={{ p: 1.25, display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))" }, gap: 0.5 }}>
          {topics.map(item => <Link key={item.slug} href={`#/help:${item.slug}`} onClick={event => { event.preventDefault(); onNavigate(`help:${item.slug}`); }} underline="hover" sx={{ borderRadius: 1, p: 1, lineHeight: 1.45 }}>{item.title[language]}</Link>)}
        </Paper>
      </Box>;
    })}</Stack>}
  </Box>;
}
