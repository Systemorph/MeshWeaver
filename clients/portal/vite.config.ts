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
    // The renderer source resolves its deps from clients/react/node_modules; dedupe so a single React
    // instance is used at runtime (the source alias can otherwise pull two copies).
    dedupe: ["react", "react-dom", "@fluentui/react-components"],
    alias: {
      "@meshweaver/react/core": path.resolve(here, "../react/src/core.ts"),
      "@meshweaver/react": path.resolve(here, "../react/src/index.tsx"),
      // The live transport (gRPC-web Connect+Deliver split); its generated stubs come from
      // `npm run gen` in clients/grpc-web, its deps resolve from clients/grpc-web/node_modules.
      "@meshweaver/client-web": path.resolve(here, "../grpc-web/src/index.ts"),
    },
  },
});
