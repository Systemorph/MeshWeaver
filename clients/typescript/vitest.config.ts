import { defineConfig } from "vitest/config";

// Tests live outside src/ so `tsc` (build/typecheck, include: src/**) does not ship them in dist.
export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
  },
});
