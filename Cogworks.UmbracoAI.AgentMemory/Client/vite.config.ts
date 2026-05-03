import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/index.ts",
      formats: ["es"],
      fileName: "cogworks-umbracoai-agentmemory",
    },
    outDir: "../wwwroot/App_Plugins/CogworksUmbracoAIAgentMemory",
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      // Bellissima provides @umbraco-cms/backoffice/* via import maps at runtime.
      // Do NOT bundle them — they must remain external.
      external: [/^@umbraco/],
    },
  },
  publicDir: "public",
});
