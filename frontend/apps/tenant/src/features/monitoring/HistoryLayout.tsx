import type { ReactNode } from "react";
import { Button, Stack, Typography } from "@mui/material";
import { FilterPanel, ResultsPanel } from "@workerdock/ui";

export function HistoryFilters({ title, children, onClear, clearLabel, language = "en" }: { title: string; children: ReactNode; onClear?: () => void; clearLabel?: string; language?: "en" | "pt-BR" }) {
  return <FilterPanel title={title} actions={onClear && <Button onClick={onClear}>{clearLabel ?? (language === "pt-BR" ? "Limpar filtros" : "Clear filters")}</Button>}>{children}</FilterPanel>;
}

export function HistoryResults({ title, updatedAt, refreshLabel, onRefresh, footer, children }: { title: string; updatedAt?: ReactNode; refreshLabel: string; onRefresh: () => void; footer?: ReactNode; children: ReactNode }) {
  return <ResultsPanel title={title} actions={<Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ alignItems: { sm: "center" } }}>
    {updatedAt && <Typography variant="caption" color="text.secondary">{updatedAt}</Typography>}
    <Button onClick={onRefresh}>{refreshLabel}</Button>
  </Stack>} footer={footer}>{children}</ResultsPanel>;
}
