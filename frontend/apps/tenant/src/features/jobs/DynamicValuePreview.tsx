import { useEffect, useRef, useState } from "react";
import { Alert, Button, Stack, Typography } from "@mui/material";
import type { Labels } from "@workerdock/ui";
import {
  previewDynamicValues,
  type DynamicValuePreviewRequest,
  type DynamicValuePreviewResponse,
} from "./dynamicValueApi";

export function DynamicValuePreview({
  request,
  t,
}: {
  request: DynamicValuePreviewRequest | null;
  t: Labels;
}) {
  const generation = useRef(0);
  const [result, setResult] = useState<DynamicValuePreviewResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    generation.current++;
    setResult(null);
    setFailed(false);
    setBusy(false);
  }, [request]);

  const preview = async () => {
    if (!request || busy) return;
    const current = ++generation.current;
    setBusy(true);
    setFailed(false);
    try {
      const response = await previewDynamicValues(request);
      if (generation.current === current) setResult(response);
    } catch {
      if (generation.current === current) setFailed(true);
    } finally {
      if (generation.current === current) setBusy(false);
    }
  };

  return (
    <Stack spacing={1}>
      <Button onClick={() => void preview()} disabled={!request || busy}>
        {t.previewDynamicValues}
      </Button>
      {failed && <Alert severity="error">{t.previewDynamicValuesError}</Alert>}
      {result && (
        <Alert severity={result.issues.length ? "warning" : "info"}>
          <Stack spacing={1}>
            <Typography>{t.previewSampleContext.replace("{sampleAtUtc}", request?.sampleAtUtc ?? "")}</Typography>
            <Typography>{t.dynamicExampleOnly}</Typography>
            {result.issues.map((issue, index) => (
              <Typography key={`${issue.path}-${issue.code}-${index}`}>
                {issue.path}: {issue.code}
              </Typography>
            ))}
            {result.issues.length === 0 &&
              Object.entries(result.values).map(([path, value]) => (
                <Typography key={path}>
                  {path}: {typeof value === "string" ? value : JSON.stringify(value)}
                </Typography>
              ))}
          </Stack>
        </Alert>
      )}
    </Stack>
  );
}
