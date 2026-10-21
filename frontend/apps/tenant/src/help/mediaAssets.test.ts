import { describe, expect, it } from "vitest";

const articleModules = import.meta.glob<string>("./content/{en,pt-BR}/*.md", { eager: true, query: "?raw", import: "default" });
const localMedia = import.meta.glob<string>("./media/*.{svg,png}", { eager: true, query: "?url", import: "default" });
const articles = Object.entries(articleModules).map(([path, source]) => ({ path, source }));
const images = articles.flatMap(article =>
  [...article.source.matchAll(/!\[([^\]]*)\]\(([^)]+)\)/g)].map(match => ({ alt: match[1], src: match[2], article })),
);

describe("documentation media", () => {
  it("includes the five required workflow diagrams", () => {
    const names = ["first-job", "execution-lifecycle", "input-choice", "schedule-flow", "alert-delivery"];
    for (const name of names) {
      expect(images.some(image => image.src === `./media/${name}.svg`), `${name} is referenced by a guide`).toBe(true);
      expect(localMedia[`./media/${name}-mobile.svg`], `${name} has a narrow reflow`).toBeDefined();
    }
  });

  it("resolves every referenced image locally with alt text and a localized figure caption", () => {
    expect(images.length).toBeGreaterThanOrEqual(5);
    for (const image of images) {
      expect(image.alt.trim(), `${image.src} has useful alt text`).not.toBe("");
      expect(image.src, `${image.src} is local`).toMatch(/^\.\/media\/[a-z0-9][a-z0-9-]*\.(svg|png)$/i);
      expect(localMedia[image.src], `${image.src} exists`).toBeDefined();
      const lines = image.article.source.split(/\r?\n/);
      const imageLine = lines.findIndex(line => line.includes(`](${image.src})`));
      const following = lines.slice(imageLine + 1, imageLine + 4).join("\n");
      const captionLabel = image.article.path.includes("/pt-BR/") ? "Figura" : "Figure";
      expect(following, `${image.src} has an italic ${captionLabel} caption`).toMatch(new RegExp(`\\*${captionLabel}:`, "i"));
    }
  });

  it("keeps screenshot count at three or fewer and blocks remote images", () => {
    expect(new Set(images.filter(image => image.src.toLowerCase().endsWith(".png")).map(image => image.src)).size).toBeLessThanOrEqual(3);
    for (const article of articles) expect(article.source, article.path).not.toMatch(/!\[[^\]]*\]\(https?:\/\//i);
  });
});
