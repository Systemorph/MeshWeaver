import { defineConfig, devices } from "@playwright/test";

// The NEXT-ONLY portal e2e: mesh backend WITHOUT Blazor + the Next.js frontend, booted together.
//
//   npm run e2e:next-only        (from clients/portal-next)
//
// webServer[0] — the Monolith with the Blazor shell OFF (Features__Gui__Blazor=false). Its /alive
//   comes up only when the mesh is served, so the wait is a real readiness signal.
// webServer[1] — `next dev` with PORTAL_ORIGIN rewrites (the DEPLOY.md local topology): /api/*,
//   the gRPC-web service and /static/* proxy to the portal, exactly like behind the shared ingress.
//
// The Monolith boot builds nothing (dotnet run compiles on demand) — first run takes minutes;
// keep reuseExistingServer for local iteration.
export default defineConfig({
  testDir: ".",
  testMatch: "next-only.spec.ts",
  timeout: 90_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  reporter: [["list"]],
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      command:
        "dotnet run --project ../../../memex/Memex.Portal.Monolith --no-launch-profile",
      cwd: ".",
      url: "http://localhost:5022/healthz",
      reuseExistingServer: !process.env.CI,
      timeout: 600_000,
      stdout: "pipe",
      env: {
        ASPNETCORE_URLS: "http://localhost:5022",
        // --no-launch-profile defaults the environment to Production; DevLogin (the suite's
        // sign-in) and the dev static-asset pipeline are Development behaviours.
        ASPNETCORE_ENVIRONMENT: "Development",
        Features__Gui__Blazor: "false",
      },
    },
    {
      command: "npm run dev",
      cwd: "..",
      url: "http://localhost:3300/next",
      reuseExistingServer: !process.env.CI,
      timeout: 300_000,
      stdout: "pipe",
      env: {
        PORTAL_ORIGIN: "http://localhost:5022",
        PORT: "3300",
      },
    },
  ],
});
