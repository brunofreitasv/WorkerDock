export type ArticleHeading = { level: 2 | 3; id: string; title: string };

function headingId(title: string): string {
  return title
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "") || "section";
}

export function parseArticleHeadings(source: string): ArticleHeading[] {
  const usedIds = new Set<string>();
  const headings: ArticleHeading[] = [];
  const addHeading = (level: 2 | 3, title: string) => {
    const baseId = headingId(title);
    let id = baseId;
    let suffix = 2;
    while (usedIds.has(id)) id = `${baseId}-${suffix++}`;
    usedIds.add(id);
    headings.push({ level, id, title });
  };
  let fence: { marker: "`" | "~"; length: number } | undefined;
  const lines = source.split(/\r?\n/);
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const fenceMatch = line.match(/^ {0,3}(`{3,}|~{3,})/);
    if (fenceMatch) {
      const marker = fenceMatch[1][0] as "`" | "~";
      if (!fence) {
        fence = { marker, length: fenceMatch[1].length };
      } else if (marker === fence.marker && fenceMatch[1].length >= fence.length) {
        fence = undefined;
      }
      continue;
    }
    if (fence) continue;
    const atx = line.match(/^ {0,3}(#{2,3})\s+(.+?)\s*#*\s*$/);
    if (atx) {
      addHeading(atx[1].length as 2 | 3, atx[2].trim());
      continue;
    }
    if (index + 1 < lines.length && /^ {0,3}-{3,}\s*$/.test(lines[index + 1]) && line.trim()) {
      addHeading(2, line.trim());
    }
  }
  return headings;
}
