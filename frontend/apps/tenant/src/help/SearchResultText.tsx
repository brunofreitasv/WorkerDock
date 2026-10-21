import type { ReactElement } from "react";

type Range = { start: number; end: number };

function normalizedWithOffsets(value: string): { text: string; offsets: number[] } {
  let text = "";
  const offsets: number[] = [];
  for (let index = 0; index < value.length;) {
    const point = String.fromCodePoint(value.codePointAt(index)!);
    const folded = point.normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLocaleLowerCase();
    for (const character of folded) {
      text += character;
      offsets.push(index);
    }
    index += point.length;
  }
  return { text, offsets };
}

function highlightRanges(value: string, query: string): Range[] {
  const source = normalizedWithOffsets(value);
  const terms = normalizedWithOffsets(query.trim()).text.split(/\s+/).filter(Boolean);
  const ranges: Range[] = [];
  for (const term of terms) {
    let at = source.text.indexOf(term);
    while (at >= 0) {
      const start = source.offsets[at];
      const end = source.offsets[at + term.length - 1] + String.fromCodePoint(value.codePointAt(source.offsets[at + term.length - 1])!).length;
      ranges.push({ start, end });
      at = source.text.indexOf(term, at + term.length);
    }
  }
  return ranges.sort((a, b) => a.start - b.start || a.end - b.end).reduce<Range[]>((merged, range) => {
    const previous = merged.at(-1);
    if (previous && range.start <= previous.end) previous.end = Math.max(previous.end, range.end);
    else merged.push({ ...range });
    return merged;
  }, []);
}

export function SearchResultText({ text, query }: { text: string; query: string }): ReactElement {
  const ranges = highlightRanges(text, query);
  if (!ranges.length) return <>{text}</>;
  const parts: ReactElement[] = [];
  let cursor = 0;
  for (const [index, range] of ranges.entries()) {
    if (cursor < range.start) parts.push(<span key={`text-${index}`}>{text.slice(cursor, range.start)}</span>);
    parts.push(<mark key={`match-${index}`}>{text.slice(range.start, range.end)}</mark>);
    cursor = range.end;
  }
  if (cursor < text.length) parts.push(<span key="text-end">{text.slice(cursor)}</span>);
  return <>{parts}</>;
}
