import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  resolve: {
    // tabster ships "type": "module" but points `main` at a CommonJS bundle and publishes NO
    // `exports` map, so Node's ESM resolver lands on dist/cjs/index.cjs and cannot see the named
    // exports @fluentui/react-tabster imports (`createTabster`, `getMover`, …). Point at the real
    // ESM build — the same entry every bundler picks up via the `module` field.
    alias: { tabster: "tabster/dist/esm/index.js" },
  },
  test: {
    environment: "jsdom",
    globals: true,
    include: ["src/**/*.test.{ts,tsx}"],
    server: {
      // Fluent 9.74.6 dropped the `node` export condition that used to make Vitest load the whole
      // Fluent tree as CommonJS. It now resolves as ESM, so the tabster mis-packaging above became
      // load-fatal. Run Fluent through Vite (rather than Node's ESM loader) so the alias applies.
      deps: { inline: [/@fluentui\//] },
    },
  },
});
