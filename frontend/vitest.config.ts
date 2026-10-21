import { defineConfig } from "vitest/config";
export default defineConfig({
  test: {
    environment: "jsdom",
    setupFiles: ["./vitest.setup.ts"],
    include: [
      "apps/**/*.test.ts",
      "apps/**/*.test.tsx",
      "packages/**/*.test.tsx",
    ],
    restoreMocks: true,
  },
});
