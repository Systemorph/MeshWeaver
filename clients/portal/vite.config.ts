import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

// Monorepo dev: resolve the renderer to its source (no build/link step). A published app uses
// `@meshweaver/react` from npm instead.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@meshweaver/react/core": path.resolve(here, "../react/src/core.ts"),
      "@meshweaver/react": path.resolve(here, "../react/src/index.tsx"),
    },
  },
});
