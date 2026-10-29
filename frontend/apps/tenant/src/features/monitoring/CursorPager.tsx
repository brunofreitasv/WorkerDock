import { Button, Stack, Typography } from "@mui/material";

type Props = { previousCount: number; nextCursor: string | null; onPrevious: () => void; onNext: () => void };

export function CursorPager({ previousCount, nextCursor, onPrevious, onNext }: Props) {
  return <Stack direction="row" spacing={1} sx={{ alignItems: "center" }} aria-label="History pages">
    <Button onClick={onPrevious} disabled={previousCount === 0}>Previous</Button>
    <Typography component="span">Page {previousCount + 1}</Typography>
    <Button onClick={onNext} disabled={!nextCursor}>Next</Button>
  </Stack>;
}
