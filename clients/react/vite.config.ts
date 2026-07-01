import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev server / demo build. The library itself is built with `tsc -p tsconfig.lib.json`.
export default defineConfig({
  plugins: [react()],
  root: "src/demo",
  build: { outDir: "../../dist-demo", emptyOutDir: true },
});
