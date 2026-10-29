import { Button, Stack, Typography } from "@mui/material";

type Props = { previousCount: number; nextCursor: string | null; language: "en" | "pt-BR"; onPrevious: () => void; onNext: () => void };

export function CursorPager({ previousCount, nextCursor, language, onPrevious, onNext }: Props) {
  const text = language === "pt-BR"
    ? { label: "Páginas do histórico", previous: "Anterior", page: "Página", next: "Próxima" }
    : { label: "History pages", previous: "Previous", page: "Page", next: "Next" };
  return <Stack component="nav" aria-label={text.label} direction="row" spacing={1} sx={{ alignItems: "center" }}>
    <Button onClick={onPrevious} disabled={previousCount === 0}>{text.previous}</Button>
    <Typography component="span">{text.page} {previousCount + 1}</Typography>
    <Button onClick={onNext} disabled={!nextCursor}>{text.next}</Button>
  </Stack>;
}
