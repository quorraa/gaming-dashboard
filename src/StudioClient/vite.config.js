import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import path from "node:path";

export default defineConfig({
  root: path.resolve(__dirname),
  base: "/studio/",
  plugins: [svelte()],
  build: {
    outDir: path.resolve(__dirname, "../Monitor.Server/wwwroot/studio"),
    emptyOutDir: true,
    assetsDir: "assets"
  }
});
