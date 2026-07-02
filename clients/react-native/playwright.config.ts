import { defineConfig, devices } from "@playwright/test";

// Drives the RN app running as WEB (react-native-web) in headless Chromium — the leaf pack's native
// primitives map to DOM, so Playwright can assert what actually renders. The webServer exports the app
// (`expo export --platform web`) and serves dist/ before the tests run.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:8080",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npm run web:export && node e2e/serve.mjs 8080",
    url: "http://localhost:8080",
    reuseExistingServer: !process.env.CI,
    timeout: 300_000, // the export can take a while on a cold Metro cache
    stdout: "pipe",
  },
});
