import { Alert, Stack, Typography } from "@mui/material";

export interface SchedulePreviewItemDto { utc: string; offset: string }

export function SchedulePreview({ items }: { items: SchedulePreviewItemDto[] }) {
  if (!items.length) return null;
  return <Stack aria-label="Schedule preview" spacing={0.5}>
    {items.map((item, index) => <Alert key={`${item.utc}-${index}`} severity="info" sx={{ py: 0 }}>
      <Typography component="span" variant="body2">{item.utc} · UTC{item.offset}</Typography>
    </Alert>)}
  </Stack>;
}
