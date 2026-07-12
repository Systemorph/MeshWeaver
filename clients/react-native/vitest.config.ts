import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

// Headless render tests for the RN leaf pack. The renderer core is source-aliased (same as tsconfig
// paths), react-native is swapped for a lightweight host-component mock, and react is deduped so the
// aliased core and react-test-renderer share ONE react instance (else: invalid hook call).
export default defineConfig({
  resolve: {
    dedupe: ["react", "react-test-renderer"],
    alias: {
      "@meshweaver/react/core": fileURLToPath(new URL("../react/src/core.ts", import.meta.url)),
      "react-native": fileURLToPath(new URL("./test/react-native.mock.tsx", import.meta.url)),
      "react-native-svg": fileURLToPath(new URL("./test/react-native-svg.mock.tsx", import.meta.url)),
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
