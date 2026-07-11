import { defineConfig, devices } from "@playwright/test";

// Drives the Vite demo (src/demo, MeshAreaView over the static sample tree) in a real browser so the
// Draggable/DropTarget controls are exercised end to end. The demo's event log reflects emitted
// MeshEvents, which the drag/drop spec asserts against.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:5173",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: "pipe",
  },
});
