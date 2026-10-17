import { defineConfig } from "vite";
export default defineConfig({
  build: {
    outDir: "../../../src/WorkerDock.Web/wwwroot",
    emptyOutDir: true,
    rolldownOptions: {
      // This app is entirely client rendered; dependency RSC directives have no server boundary.
      checks: { moduleLevelDirective: false },
      output: {
        codeSplitting: {
          groups: [
            { name: "react", test: /node_modules[\\/]react(?:-dom)?[\\/]/ },
            {
              name: "material",
              test: /node_modules[\\/]@(?:mui|emotion)[\\/]/,
            },
          ],
        },
      },
    },
  },
});
