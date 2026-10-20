import { useEffect, useState } from "react";
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography } from "@mui/material";
import type { Labels } from "@workerdock/ui";

export function RunNowDialog({
  open,
  timeZoneId,
  supportsReferenceDate,
  busy,
  onClose,
  onConfirm,
  t,
}: {
  open: boolean;
  timeZoneId: string;
  supportsReferenceDate: boolean;
  busy: boolean;
  onClose(): void;
  onConfirm(referenceDateOverride: string | null): Promise<void>;
  t: Labels;
}) {
  const [referenceDate, setReferenceDate] = useState("");
  useEffect(() => {
    if (open) setReferenceDate("");
  }, [open]);

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose}>
      <DialogTitle>{t.runNow}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography>{t.runNowReferenceDatePrompt.replace("{timeZoneId}", timeZoneId)}</Typography>
          {supportsReferenceDate ? (
            <TextField
              type="date"
              label={t.dynamicReferenceDate}
              value={referenceDate}
              onChange={event => setReferenceDate(event.target.value)}
            />
          ) : (
            <Typography role="status">{t.runNowReferenceDateUnavailable}</Typography>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>{t.cancel}</Button>
        <Button
          onClick={() => void onConfirm(supportsReferenceDate && referenceDate ? referenceDate : null)}
          disabled={busy}
        >
          {t.runNow}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
