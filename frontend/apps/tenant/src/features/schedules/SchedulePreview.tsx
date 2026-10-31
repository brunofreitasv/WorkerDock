import { Alert, Stack, Typography } from "@mui/material";
import type { Language, Labels } from "@workerdock/ui";
import { formatInstant } from "../../localTime";

export interface SchedulePreviewItemDto { utc: string; offset: string }

export function SchedulePreview({ items, clientTimeZone, language, scheduleTimeZone, t }: { items: SchedulePreviewItemDto[]; clientTimeZone: string; language: Language; scheduleTimeZone: string; t: Labels }) {
  if (!items.length) return null;
  return <Stack aria-label="Schedule preview" spacing={0.5}>
    {items.map((item, index) => <Alert key={`${item.utc}-${index}`} severity="info" sx={{ py: 0 }}>
      <Stack spacing={0.25}>
        <Typography component="span" variant="body2">{t.previewClientTime}: {formatInstant(item.utc, language, clientTimeZone)}{scheduleTimeZone === clientTimeZone ? ` · ${item.offset}` : ""}</Typography>
        {scheduleTimeZone !== clientTimeZone && <Typography component="span" variant="body2">{t.previewScheduleTime}: {formatInstant(item.utc, language, scheduleTimeZone)} · {scheduleTimeZone} {item.offset}</Typography>}
        <Typography component="span" variant="caption">{t.previewTechnicalUtc}: {item.utc}</Typography>
      </Stack>
    </Alert>)}
  </Stack>;
}
