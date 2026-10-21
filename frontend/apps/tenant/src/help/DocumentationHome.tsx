import { useMemo, useState, type ReactElement } from "react";
import { Box, Button, Chip, Paper, Stack, TextField, Typography } from "@mui/material";
import type { Language } from "@workerdock/ui";
import { helpGroups, helpTopics } from "./topics";
import { searchHelpTopics } from "./search";
import { documentationCopy } from "./documentationCopy";
import { SearchResultText } from "./SearchResultText";

type Props = {
  language: Language;
  canManage: boolean;
  onNavigate: (route: string) => void;
};

export function DocumentationHome({ language, canManage, onNavigate }: Props): ReactElement {
  const copy = documentationCopy[language];
  const [query, setQuery] = useState("");
  const results = useMemo(() => searchHelpTopics(language, query), [language, query]);
  const shortcuts = [
    { slug: "first-job", title: copy.firstJob },
    { slug: "monitoring", title: copy.monitor },
    { slug: "sql-jobs", title: copy.sqlJob },
    { slug: "alert-rules", title: copy.alertRule },
  ];
  const topicCard = (topic: typeof helpTopics[number]) => (
    <Paper key={topic.slug} component="article" variant="outlined" sx={{ p: 2, height: "100%", transition: "border-color 120ms ease, box-shadow 120ms ease", "&:hover": { borderColor: "primary.main", boxShadow: 1 } }}>
      <Stack spacing={1} sx={{ height: "100%", alignItems: "flex-start" }}>
        <a href={`#/help:${topic.slug}`} onClick={event => { event.preventDefault(); onNavigate(`help:${topic.slug}`); }} style={{ fontWeight: 600, lineHeight: 1.4 }}>{topic.title[language]}</a>
        <Typography variant="body2" color="text.secondary" sx={{ flex: 1 }}>{topic.summary[language]}</Typography>
        <Chip size="small" variant="outlined" color={topic.requiredRole === "Manage" ? "primary" : "default"} label={topic.requiredRole === "Manage" ? copy.manage : copy.read} />
      </Stack>
    </Paper>
  );

  return (
    <Box component="main" sx={{ width: "100%", maxWidth: 1120, mx: "auto", px: { xs: 2, sm: 3 }, pb: 5 }}>
      <Box sx={{ maxWidth: 760, mb: 3 }}>
        <Typography component="h1" variant="h3" sx={{ fontSize: { xs: "2rem", sm: "2.5rem" }, fontWeight: 700, mb: 1 }}>{copy.title}</Typography>
        <Typography color="text.secondary" sx={{ fontSize: "1.05rem", lineHeight: 1.65 }}>{copy.intro}</Typography>
      </Box>

      <Paper component="section" aria-label={copy.search} variant="outlined" sx={{ p: { xs: 2, sm: 2.5 }, mb: 3, bgcolor: "background.paper", borderTop: "3px solid", borderTopColor: "primary.main" }}>
        <Box sx={{ display: "flex", flexDirection: { xs: "column", sm: "row" }, alignItems: { xs: "stretch", sm: "flex-start" }, gap: 1.5 }}>
          <TextField autoFocus={false} type="search" fullWidth label={copy.search} value={query} onChange={event => setQuery(event.target.value)} helperText={query ? undefined : copy.searchHint} />
          {query && <Button onClick={() => setQuery("")} sx={{ minHeight: 48, flexShrink: 0 }}>{copy.clear}</Button>}
        </Box>
      </Paper>

      {!query.trim() ? <>
        <Box component="section" aria-labelledby="documentation-shortcuts" sx={{ mb: 4 }}>
          <Typography id="documentation-shortcuts" component="h2" variant="h5" sx={{ mb: 1.5, fontWeight: 650 }}>{copy.shortcuts}</Typography>
          <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))", lg: "repeat(4, minmax(0, 1fr))" }, gap: 1.5 }}>
            {shortcuts.map(shortcut => {
              const topic = helpTopics.find(item => item.slug === shortcut.slug);
              return topic ? <Paper key={shortcut.slug} component="a" href={`#/help:${topic.slug}`} variant="outlined" onClick={event => { event.preventDefault(); onNavigate(`help:${topic.slug}`); }} sx={{ display: "flex", minHeight: 88, alignItems: "center", p: 2, color: "primary.main", fontWeight: 650, textDecoration: "none", lineHeight: 1.4, "&:hover": { borderColor: "primary.main", bgcolor: "action.hover" }, "&:focus-visible": { outline: "3px solid", outlineColor: "primary.main", outlineOffset: 2 } }}>{shortcut.title}</Paper> : null;
            })}
          </Box>
        </Box>
        <Stack spacing={3}>
          {helpGroups.map(group => {
            const topics = helpTopics.filter(topic => topic.group === group.id);
            return <Box key={group.id} component="section" aria-labelledby={`documentation-group-${group.id}`}>
              <Typography id={`documentation-group-${group.id}`} component="h2" variant="h5" sx={{ mb: 1.5, fontWeight: 650 }}>{group.title[language]}</Typography>
              <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", sm: "repeat(2, minmax(0, 1fr))", lg: "repeat(3, minmax(0, 1fr))" }, gap: 1.5 }}>{topics.map(topicCard)}</Box>
            </Box>;
          })}
        </Stack>
      </> : <Box component="section" role="region" aria-label={copy.results}>
        <Typography component="h2" variant="h5" sx={{ mb: 1.5, fontWeight: 650 }}>{copy.results}</Typography>
        {results.length ? <Stack component="ul" spacing={1.5} sx={{ p: 0, m: 0, listStyle: "none" }}>{results.map(({ topic, excerpt }) => <Box component="li" key={topic.slug}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Stack spacing={0.75}>
              <Box sx={{ display: "flex", gap: 1, alignItems: "center", flexWrap: "wrap" }}>
                <a href={`#/help:${topic.slug}`} onClick={event => { event.preventDefault(); onNavigate(`help:${topic.slug}`); }} style={{ fontWeight: 650 }}><SearchResultText text={topic.title[language]} query={query} /></a>
                <Typography variant="caption" color="text.secondary"><SearchResultText text={groupTitle(topic.group, language)} query={query} /></Typography>
                <Chip size="small" variant="outlined" label={topic.requiredRole === "Manage" ? copy.manage : copy.read} />
              </Box>
              <Typography variant="body2" sx={{ fontWeight: 500 }}><SearchResultText text={topic.summary[language]} query={query} /></Typography>
              <Typography variant="body2" color="text.secondary"><SearchResultText text={excerpt} query={query} /></Typography>
            </Stack>
          </Paper>
        </Box>)}</Stack> : <Paper variant="outlined" sx={{ p: 3 }}>
          <Typography component="h3" variant="h6" sx={{ mb: 0.5 }}>{copy.noResults}</Typography>
          <Typography color="text.secondary">{copy.noResultsHint}</Typography>
        </Paper>}
      </Box>}
    </Box>
  );
}

function groupTitle(groupId: typeof helpGroups[number]["id"], language: Language): string {
  return helpGroups.find(group => group.id === groupId)?.title[language] ?? "";
}
