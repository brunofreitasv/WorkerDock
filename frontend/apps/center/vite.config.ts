import { defineConfig } from "vite";
export default defineConfig({
  build: {
    outDir: "../../../src/WorkerDock.ControlCenter/wwwroot",
    emptyOutDir: true,
    rolldownOptions: {
      checks: { moduleLevelDirective: false },
      output: {
        codeSplitting: {
          groups: [
            { name: "react", test: /node_modules[\\/]react(?:-dom)?[\\/]/ },
            { name: "material", test: /node_modules[\\/]@(?:mui|emotion)[\\/]/ },
          ],
        },
      },
    },
  },
});
