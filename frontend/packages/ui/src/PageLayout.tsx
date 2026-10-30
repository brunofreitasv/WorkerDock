import type { ReactNode } from "react";
import { Box, Chip, Paper, Stack, Typography } from "@mui/material";

type Actions = { actions?: ReactNode };
export function PageHeader({ title, description, actions }: { title: string; description?: string } & Actions) {
  return <Stack direction={{ xs: "column", sm: "row" }} sx={{ justifyContent: "space-between", alignItems: { sm: "flex-start" }, gap: 2, mb: 3 }}>
    <Box><Typography component="h1" variant="h4">{title}</Typography>{description && <Typography color="text.secondary" sx={{ mt: 0.75 }}>{description}</Typography>}</Box>
    {actions && <Stack direction={{ xs: "column", sm: "row" }} sx={{ gap: 1 }}>{actions}</Stack>}
  </Stack>;
}
export function SectionSurface({ title, description, actions, children }: { title?: string; description?: string; children: ReactNode } & Actions) {
  return <Paper component="section" aria-label={title} variant="outlined" sx={{ p: { xs: 2, sm: 3 }, mb: 3 }}>
    {(title || description || actions) && <Stack direction={{ xs: "column", sm: "row" }} sx={{ justifyContent: "space-between", gap: 2, mb: 2 }}>
      <Box>{title && <Typography component="h2" variant="h6">{title}</Typography>}{description && <Typography color="text.secondary">{description}</Typography>}</Box>{actions}
    </Stack>}{children}
  </Paper>;
}
export function FilterPanel({ title, children, actions }: { title: string; children: ReactNode } & Actions) {
  return <SectionSurface title={title} actions={actions}>{children}</SectionSurface>;
}
export function ResultsPanel({ title, meta, actions, children, footer }: { title: string; meta?: ReactNode; children: ReactNode; footer?: ReactNode } & Actions) {
  return <SectionSurface title={title} description={typeof meta === "string" ? meta : undefined} actions={actions}>
    {typeof meta !== "string" && meta}{children}{footer && <Box sx={{ mt: 2, pt: 2, borderTop: "1px solid", borderColor: "divider" }}>{footer}</Box>}
  </SectionSurface>;
}
export function StatusBadge({ label, tone }: { label: string; tone: "default" | "primary" | "success" | "warning" | "error" | "info" }) {
  return <Chip label={label} color={tone} size="small" variant="outlined" sx={{ fontWeight: 700 }} />;
}
export function EmptyState({ title, description }: { title: string; description?: string }) {
  return <Box sx={{ textAlign: "center", py: 5, px: 2 }}><Typography component="h3" variant="h6">{title}</Typography>{description && <Typography color="text.secondary" sx={{ mt: 1 }}>{description}</Typography>}</Box>;
}
