import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

// Mirrors next.config.mjs: renderer + transport aliased to SOURCE, single react/fluent copy.
// "server-only" is a Next-bundler guard package (its default export throws outside a server
// bundle) — stubbed here so the server snapshot module is unit-testable.
export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ["react", "react-dom", "@fluentui/react-components"],
    alias: {
      "@meshweaver/react/wire": path.resolve(here, "../react/src/live/wire.ts"),
      "@meshweaver/react/accessError": path.resolve(here, "../react/src/area/accessError.ts"),
      "@meshweaver/react/core": path.resolve(here, "../react/src/core.ts"),
      "@meshweaver/react": path.resolve(here, "../react/src/index.tsx"),
      "@meshweaver/client-web": path.resolve(here, "../grpc-web/src/index.ts"),
      "server-only": path.resolve(here, "test/stubs/server-only.ts"),
      // The app-router hooks need Next's AppRouterContext (absent in a bare render) — stub so
      // LiveArea (useRouter for the access-denied redirect) is unit-testable.
      "next/navigation": path.resolve(here, "test/stubs/next-navigation.ts"),
    },
  },
  test: {
    environment: "node", // per-file overrides via "@vitest-environment jsdom" docblocks
    globals: true,
    include: ["test/**/*.test.{ts,tsx}"],
  },
});
