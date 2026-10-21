const bundledMedia = import.meta.glob<string>("./media/*.{svg,png}", {
  eager: true,
  query: "?url",
  import: "default",
});

export function getHelpMedia(src: string): string | undefined {
  if (!/^\.\/media\/[a-z0-9][a-z0-9-]*\.(?:svg|png)$/i.test(src)) return undefined;
  return bundledMedia[src];
}
