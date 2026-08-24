import { expect, test } from "@playwright/test";

// NEXT-ONLY portal mode, end to end: the mesh backend runs with Features__Gui__Blazor=false — no
// Razor-components pipeline, no circuit, no Blazor view registry — and the Next.js app is the
// entire GUI (dev server with PORTAL_ORIGIN rewrites, exactly the DEPLOY.md local topology). The
// two servers are booted by playwright.config's webServer entries.
//
// What this proves, in order: the backend genuinely serves NO Blazor; the mesh surfaces the JS
// shell consumes still work — auth policies included; and the Next frontend renders real mesh
// content through them for a signed-in visitor.

const portal = "http://localhost:5022";
const next = "http://localhost:3300";

test("the portal serves NO Blazor: circuit endpoints are absent", async ({ request }) => {
  // The circuit's negotiate endpoint is the Blazor tell — mapped by MapRazorComponents/
  // MapBlazorHub, absent in next-only mode.
  const negotiate = await request.post(`${portal}/_blazor/negotiate?negotiateVersion=1`, { failOnStatusCode: false });
  expect(negotiate.status()).toBe(404);
  // The framework asset prefix 404s too — the static-asset manifest still lists blazor.web.js
  // (the assemblies stay referenced), and without the gate its dev endpoint 500s.
  const framework = await request.get(`${portal}/_framework/blazor.web.js`, { failOnStatusCode: false });
  expect(framework.status()).toBe(404);
});

test("a browser navigation on the portal origin lands on the Next shell", async ({ request }) => {
  const r = await request.get(`${portal}/Doc/Architecture`, {
    maxRedirects: 0,
    failOnStatusCode: false,
    headers: { accept: "text/html,application/xhtml+xml" },
  });
  expect(r.status()).toBe(302);
  expect(r.headers()["location"]).toBe("/next/Doc/Architecture");
});

test("the mesh surfaces the JS shells consume still work without Blazor", async ({ request }) => {
  // whoami is cookie-OR-bearer gated (ReadPolicy) — anonymous is 401 BY DESIGN, with or
  // without Blazor. The auth pipeline holding is part of what this asserts.
  const anonymous = await request.post(`${portal}/api/mesh/whoami`, { data: {}, failOnStatusCode: false });
  expect(anonymous.status()).toBe(401);
  // Sign in through DevLogin (Monolith, Authentication:EnableDevLogin) — the cookie lands in
  // this request context's jar and authenticates the reads below.
  // maxRedirects 0: the 302 lands on `/`, which in next-only mode 302s again to /next — a path
  // the PORTAL origin deliberately answers 404 (it belongs to the Next server). Following the
  // chain would report that terminal 404 and hide the successful sign-in.
  const login = await request.post(`${portal}/dev/signin`, {
    form: { personId: "rbuergi" },
    maxRedirects: 0,
    failOnStatusCode: false,
  });
  expect(login.status()).toBe(302);
  const who = await request.post(`${portal}/api/mesh/whoami`, { data: {} });
  expect(who.status()).toBe(200);
  // The resolve verb (navigation resolution — portal-next's SSR depends on it).
  const resolve = await request.post(`${portal}/api/mesh/resolve`, { data: { path: "Doc" } });
  expect(resolve.status()).toBe(200);
  // Static assets (node icons) still serve — they are shell-independent.
  const icon = await request.get(`${portal}/static/NodeTypeIcons/book.svg`);
  expect(icon.status()).toBe(200);
  expect(icon.headers()["content-type"]).toContain("svg");
});

test("the Next frontend renders real mesh content", async ({ page }) => {
  // Watch for anything Blazor-shaped from the very first request — a listener attached after
  // goto would miss the load itself.
  const blazorRequests: string[] = [];
  page.on("request", (r) => {
    if (r.url().includes("_blazor") || r.url().includes("blazor.web.js")) blazorRequests.push(r.url());
  });
  // Sign in via DevLogin: page.request shares the browser context's cookie jar, and localhost
  // cookies ignore the port, so the cookie minted against :5022 rides to the :3300 page (whose
  // dev server forwards it to the portal via the PORTAL_ORIGIN rewrites).
  const login = await page.request.post(`${portal}/dev/signin`, {
    form: { personId: "rbuergi" },
    maxRedirects: 0,
    failOnStatusCode: false,
  });
  expect(login.status()).toBe(302);
  await page.goto(`${next}/next/Doc`, { waitUntil: "domcontentloaded" });
  // Real mesh content appears (a fresh Monolith renders the Doc space's home — the page title is
  // the space name), under the SIGNED-IN chrome (the user-profile button only renders for a
  // resolved identity)…
  await expect(page.getByRole("heading", { name: "Doc" })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("button", { name: "User profile" })).toBeVisible({ timeout: 30_000 });
  // …with the LIVE connection up: this banner appears when the live-source token mint fails,
  // which is exactly what a Blazor-shell dependency left in the API path caused before the fix.
  await expect(page.getByText("No live mesh connection")).toHaveCount(0);
  expect(blazorRequests).toEqual([]);
});
