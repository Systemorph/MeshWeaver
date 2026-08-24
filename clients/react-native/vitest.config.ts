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
      "expo-video": fileURLToPath(new URL("./test/expo-video.mock.tsx", import.meta.url)),
      // Every expo package code-under-test imports needs a mock: the REAL entries — SDK 57
      // included — read `globalThis.expo.EventEmitter` (expo-modules-core) at import, which only a
      // real Expo runtime provides. Verified empirically on 57: aliasing them away and importing
      // the real expo-constants kills every suite that touches ./connection at LOAD time.
      // (expo-av is gone on 57 — expo-audio/expo-video replaced it, mocked likewise.)
      "expo-audio": fileURLToPath(new URL("./test/expo-audio.mock.ts", import.meta.url)),
      "expo-constants": fileURLToPath(new URL("./test/expo-constants.mock.ts", import.meta.url)),
      "expo-auth-session": fileURLToPath(new URL("./test/expo-auth.mock.ts", import.meta.url)),
      "expo-web-browser": fileURLToPath(new URL("./test/expo-auth.mock.ts", import.meta.url)),
      expo: fileURLToPath(new URL("./test/expo.mock.ts", import.meta.url)),
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
