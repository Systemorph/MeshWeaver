---
Name: Getting Started with the React Frontend
Category: Documentation
Description: Run the React frontend — the served SPA at /app, the Portal:Frontend / Portal:ReactAppUrl configuration, the /frontend toggle endpoints + mw-frontend cookie, and local dev with Vite.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="5 3 19 12 5 21 5 3"/></svg>
---

# Getting Started with the React Frontend

There are two ways to run the React frontend: **served by a portal** (the SPA at `/app`, sharing the portal's origin, auth, and gRPC-web endpoint) and **standalone with Vite** (local development against sample data or a remote mesh).

## The served SPA at `/app`

A portal deployment serves the built React app from its own `wwwroot/app` — the Vite production bundle (built with base `/app/`) copied into the portal's static files. Two pieces of middleware in the portal pipeline (`Memex.Portal.Shared/MemexConfiguration.cs`) make this work:

1. **The SPA rewrite** — extension-less `/app` paths are rewritten to `/app/index.html` *before* static files run, so client-side routes deep inside the SPA always land on the bundle's entry page instead of Blazor's page catch-all:

```csharp
// React GUI SPA: rewrite extension-less /app paths to the SPA entry BEFORE static files.
app.Use((ctx, next) =>
{
    var p = ctx.Request.Path.Value;
    if (p is not null
        && (p.Equals("/app", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
        && !System.IO.Path.HasExtension(p))
        ctx.Request.Path = "/app/index.html";
    return next();
});
```

2. **The gRPC-web transport**, gated by the `Features:Grpc` flag — the browser's data plane. Three calls wire it end to end:

```csharp
// Mesh tier (ConfigureMemexMesh): registers the gRPC service + declares the
// foreign-participant address types (py/*, node/*) stream-routed.
if (features.Grpc)
    mb = mb.AddGrpcHub();

// Request pipeline (between UseRouting and the endpoint maps):
if (features.Grpc)
    app.UseMeshWeaverGrpcWeb();     // gRPC-web middleware — browsers can't do HTTP/2 bidi

// Endpoints:
if (features.Grpc)
    app.MapMeshWeaverGrpc();        // meshweaver.v1.Mesh, grpc-web enabled
```

`AddGrpcHub` / `UseMeshWeaverGrpcWeb` / `MapMeshWeaverGrpc` live in `src/MeshWeaver.Hosting.Grpc/GrpcHostingExtensions.cs`. The `node/*` address type is what the browser connection registers under (each tab connects as participant `node/<id>`); without the stream-routing declaration, replies addressed to it would be treated as node lookups and dropped. The wire details are on [Rendering Architecture](../Rendering).

### How the served SPA connects and routes

On load the app shell (`clients/portal/src/Portal.tsx` + `live.ts`) joins the mesh over **same-origin gRPC-web**. The gRPC path is identified by a Bearer `mw_…` API token, not the browser cookie — so the SPA first mints a short-lived token off the already-authenticated session (`POST /api/tokens`, cookie-authorized, token held in memory only) and then connects. The mint response's `nodePath` (`{userId}/ApiToken/…`) reveals the signed-in user's partition, which becomes the default route — no separate who-am-I endpoint.

Routing is hash-based over mesh paths, mirroring the Blazor portal's URL shape:

| URL | Renders |
|---|---|
| `/app/#/{meshPath}` (e.g. `/app/#/Doc/GUI`) | That node's **default layout area**, live — the same area Blazor shows at `/{meshPath}` |
| `/app` (no hash) | The signed-in user's home — what Blazor shows at `/{user}` |

When no live connection can be established on the origin (standalone dev server, or an unauthenticated session), the shell falls back to bundled **sample data** with an "offline sample mode" banner — the same demo the standalone Vite server shows.

## Choosing the frontend: `Portal:Frontend` + `Portal:ReactAppUrl`

Frontend selection is implemented in `src/MeshWeaver.Blazor.Portal/FrontendSelection.cs` and is configured per deployment and overridable per user:

| Setting | Meaning |
|---|---|
| `Portal:ReactAppUrl` | Where the React app is served (e.g. `/app/`, or an absolute URL). **The whole feature is inert until this is set** — existing deployments are unaffected. |
| `Portal:Frontend` | The deployment default: `"Blazor"` (default) or `"React"`. |
| `mw-frontend` cookie | The per-user override: `React` or `Blazor`. Wins over the deployment default. |

The middleware (`UseFrontendSelection`, registered before static files/routing) redirects **interactive HTML GET navigations** to the React app when the effective frontend is React. Everything non-navigational passes through untouched: the Blazor circuit, static assets, MCP/API/SignalR/gRPC transports, health probes, auth flows, and the `/frontend` endpoints themselves. XHR/fetch requests (no `text/html` in `Accept`) and any path with a file extension also pass through.

## The toggle: `/frontend/{react|blazor|clear}` + the `mw-frontend` cookie

`MapFrontendSelection` maps one endpoint, `GET /frontend/{target}` — the reversible switch both shells link to:

| Target | Effect |
|---|---|
| `/frontend/react` | Sets `mw-frontend=React` and redirects to `Portal:ReactAppUrl` |
| `/frontend/blazor` | Sets `mw-frontend=Blazor` and redirects to `/` (the classic shell) |
| `/frontend/clear` | Deletes the cookie — the deployment default applies again |

The cookie is deliberately **not** HttpOnly (`Path=/`, `MaxAge` 365 days, `SameSite=Lax`): user preferences in MeshWeaver are client-side (the theme lives in localStorage — see [Theming](../Theming)), and the frontend choice follows the same pattern so the React app can read and clear it too.

Both shells expose the toggle in their chrome:

- **Blazor → React**: the user menu shows **"Try the new frontend"** (`UserProfile.razor`) — visible only when `FrontendSelection.IsEnabled(Configuration)`, i.e. when the deployment configured a React app URL. It navigates (full page load) to `/frontend/react`.
- **React → Blazor**: the React shell's header shows **"Back to classic"** (`clients/portal/src/Portal.tsx`), which writes the cookie client-side (so the choice sticks even when the app is served standalone, with no portal endpoint on the origin) and navigates to `/frontend/blazor`.

## Local development

The app shell lives in `clients/portal`; in the monorepo its `vite.config.ts` aliases `@meshweaver/react` to the renderer *source* (`../react/src`), so there is no build/link step and renderer edits hot-reload straight into the shell:

```bash
cd clients/portal
npm install
npm run dev        # Vite dev server → http://localhost:5173
```

With no portal on `localhost:5173`, the shell runs in offline sample mode, rendering the `{areas, data}` trees from `src/sample.ts` through a `StaticAreaSource` — every control interactive, no backend required. Served from a portal origin, the same build connects live automatically (previous section).

The renderer package itself has a richer standalone demo (~50 controls in one tree — cards, tabs, a data grid, a chart, data-bound form inputs) plus the test suites:

```bash
cd clients/react
npm install
npm run dev        # the demo area
npm test           # vitest — renderer core, controls, parity ratchet
```

To produce the bundle a portal serves, build with the `/app/` base and place the output at the portal's `wwwroot/app`:

```bash
cd clients/portal
npm run build -- --base=/app/
# dist/ → <portal project>/wwwroot/app/
```

## Related

- [React Frontend overview](/Doc/GUI/React) — architecture and parity state.
- [Rendering Architecture](../Rendering) — how the SPA gets its data.
- [Theming](../Theming) — the shared theme preference between both frontends.
