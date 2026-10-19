import { readFile, readdir, mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const lock = JSON.parse(
  await readFile(resolve(root, "package-lock.json"), "utf8"),
);
const notices = [
  "WorkerDock frontend third-party notices",
  "Generated from the pinned production dependency graph; includes transitive package notices.",
];
for (const [location, entry] of Object.entries(lock.packages)) {
  if (!location.includes("node_modules/") || entry.dev || entry.link) continue;
  const folder = resolve(root, location);
  const metadata = JSON.parse(
    await readFile(resolve(folder, "package.json"), "utf8"),
  );
  const files = (await readdir(folder))
    .filter((name) => /^(license|notice)/i.test(name))
    .sort();
  if (!files.length)
    throw new Error(`Missing dependency notice: ${metadata.name}`);
  notices.push(
    `\n${"=".repeat(72)}\n${metadata.name} ${metadata.version} (${entry.license ?? metadata.license})`,
  );
  for (const file of files)
    notices.push(await readFile(resolve(folder, file), "utf8"));
}
for (const app of ["tenant", "center"]) {
  const target = resolve(root, `apps/${app}/public/THIRD-PARTY-NOTICES.txt`);
  await mkdir(dirname(target), { recursive: true });
  await writeFile(target, notices.join("\n\n") + "\n");
}
