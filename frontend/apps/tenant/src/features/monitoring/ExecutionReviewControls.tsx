import { useState } from "react";
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography } from "@mui/material";
import { mutate } from "../../api/client";
import { translations } from "@workerdock/ui";

type Decision = "ReconcileSucceeded" | "Cancel" | "AuthorizeRetry";

export function ExecutionReviewControls({ executionId, canManage, language }: { executionId: string; canManage: boolean; language: string }) {
  const pt = language === "pt-BR";
  const t = translations[pt ? "pt-BR" : "en"];
  const [decision, setDecision] = useState<Decision | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  if (!canManage) return <Alert severity="warning" sx={{ mt: 2 }}>{t.executionNeedsReviewReadOnly}</Alert>;

  const submit = async () => {
    if (!decision || !reason.trim()) return;
    setBusy(true);
    setMessage("");
    try {
      await mutate(`/api/executions/${encodeURIComponent(executionId)}/resolve`, "POST", { decision, reason: reason.trim() });
      setMessage(t.executionReviewSubmitted);
      setDecision(null);
      setReason("");
    } catch {
      setMessage(t.executionReviewFailed);
    } finally {
      setBusy(false);
    }
  };

  const decisionText = decision === "ReconcileSucceeded" ? t.executionReviewSucceeded : decision === "Cancel" ? t.executionReviewCancel : t.executionReviewRetry;
  return <Stack spacing={1} sx={{ mt: 2 }}>
    <Alert severity="warning">{t.executionNeedsReviewHelp}</Alert>
    {message && <Alert role="status" severity={message === t.executionReviewFailed ? "error" : "info"}>{message}</Alert>}
    <Stack direction="row" spacing={1} sx={{ flexWrap: "wrap" }}>
      <Button onClick={() => setDecision("ReconcileSucceeded")}>{t.executionReviewSucceeded}</Button>
      <Button color="warning" onClick={() => setDecision("Cancel")}>{t.executionReviewCancel}</Button>
      <Button color="error" onClick={() => setDecision("AuthorizeRetry")}>{t.executionReviewRetry}</Button>
    </Stack>
    <Dialog open={decision !== null} onClose={() => !busy && setDecision(null)} aria-labelledby="execution-review-title">
      <DialogTitle id="execution-review-title">{decisionText}</DialogTitle>
      <DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
        <Typography>{decision === "ReconcileSucceeded" ? t.executionReviewSucceededHelp : decision === "Cancel" ? t.executionReviewCancelHelp : t.executionReviewRetryHelp}</Typography>
        <TextField label={t.executionReviewReason} required multiline minRows={2} maxRows={5} value={reason} onChange={event => setReason(event.target.value)} />
      </Stack></DialogContent>
      <DialogActions>
        <Button onClick={() => setDecision(null)} disabled={busy}>{t.cancel}</Button>
        <Button onClick={() => void submit()} disabled={busy || !reason.trim() || reason.length > 500}>{t.executionReviewConfirm}</Button>
      </DialogActions>
    </Dialog>
  </Stack>;
}
