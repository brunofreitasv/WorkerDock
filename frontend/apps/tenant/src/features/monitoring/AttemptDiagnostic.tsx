import { Box, Stack, Typography } from "@mui/material";
import { translations } from "@workerdock/ui";
import type { FailureDiagnostic } from "./monitoringTypes";

// Diagnostic strings are children, never markup, including provider messages and response bodies.
export function AttemptDiagnostic({ diagnostic: d, attemptId, language }: { diagnostic?: FailureDiagnostic | null; attemptId: string; language: string }) {
  const t = translations[language === "pt-BR" ? "pt-BR" : "en"];
  if (!d) return <Typography variant="caption">{t.diagnosticUnavailable}</Typography>;
  const fields: [string, string | number | null | undefined][] = [
    [t.diagnosticAttempt, attemptId], [t.diagnosticKind, d.kind],
    [t.diagnosticExceptionType, d.exceptionType],
  ];
  if (d.kind === "Sql") fields.push(
    [t.diagnosticSqlNumber, d.sqlNumber], [t.diagnosticSqlState, d.sqlState],
    [t.diagnosticSqlProcedure, d.sqlProcedure], [t.diagnosticSqlLine, d.sqlLine],
    [t.diagnosticCommandKind, d.commandKind],
  );
  if (d.kind === "Http") fields.push(
    [t.diagnosticHttpMethod, d.httpMethod],
    [t.diagnosticHttpStatus, d.httpStatus], [t.diagnosticMediaType, d.mediaType],
    [t.diagnosticResponseBytes, d.responseBytes],
  );
  return <Box component="details" sx={{ overflowWrap: "anywhere" }}>
    <Box component="summary" sx={{ cursor: "pointer" }}>{t.diagnosticTitle}</Box>
    <Stack spacing={1} sx={{ mt: 1 }}>
      {d.hidden && <Typography>{t.diagnosticHidden}</Typography>}
      {d.truncated && <Typography>{t.diagnosticTruncated}</Typography>}
      {fields.map(([label, value]) => value === null || value === undefined || value === "" ? <Typography key={label}>{label}: {t.diagnosticFieldUnavailable}</Typography> : <Box key={label}><Typography variant="caption">{label}</Typography><Typography sx={{ whiteSpace: "pre-wrap" }}>{value}</Typography></Box>)}
      {!d.hidden && <>
        {[[t.diagnosticMessage, d.message], [t.diagnosticStack, d.stackTrace], [t.diagnosticResponse, d.responseContent]].map(([label, value]) => value ? <Box key={label}><Typography variant="caption">{label}</Typography><Box component="pre" sx={{ m: 0, whiteSpace: "pre-wrap", overflowWrap: "anywhere", maxHeight: 320, overflow: "auto" }}>{value}</Box></Box> : <Typography key={label}>{label}: {t.diagnosticFieldUnavailable}</Typography>)}
      </>}
    </Stack>
  </Box>;
}
