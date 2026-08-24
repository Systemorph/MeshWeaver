# RN app deployments — build-time composition

The JS bundle is static: Hermes loads no code at runtime, so a mesh module cannot inject client
code into a RUNNING app the way its server half joins the mesh. **The deployment is the
composition point instead**: one manifest per deployment declares the app variant, and the build
consumes it. Three layers, from zero-effort up:

1. **Most module UI needs nothing here.** Server-declared controls render through the base pack —
   a module's pages, areas, forms, grids and catalogs work on the app with **no app change at
   all**. Reach for a client module only for a bespoke LEAF (a chess board, a custom chart).
2. **A deployment manifest** (`deployment/<name>.json`, schema in `deployment.schema.json`)
   declares: the portal a native build dials, the branding (display name, light/dark accent), and
   the client **modules** in the bundle. Select one with `MEMEX_DEPLOYMENT=<name|path>`; unset =
   `default.json` (the stock Memex app).
3. **A client module** is any Metro-resolvable import (npm package or repo path) that
   default-exports a `DeploymentModule` (`@meshweaver/react/core`): today `pack`
   (controls/skins folded over the base pack — later modules win); the shape is open for future
   slots (screens, menu items, speech providers).

`scripts/gen-deployment.mjs` turns the manifest into **static imports**
(`src/deployment.generated.ts`) — that file IS the deployment at runtime. It regenerates
automatically before `start` / `ios` / `android` / `web` / `web:export` (npm pre-scripts), and the
default output is checked in so a fresh clone and CI build without running anything.

The sidecar bake (`Memex.LocalMesh` → `BakeRnWebClient`) honors it too:

```bash
dotnet publish memex/Memex.LocalMesh -c Release -p:RnDeployment=deployment/examples/acme.json
# or: MEMEX_DEPLOYMENT=acme dotnet publish …
```

**The stock app is itself composed this way** — the core pack is the PLATFORM (layout, skins,
inputs, markdown, icons, navigation plumbing), and every product domain is a standard module the
default manifest lists: `threads` (chat + composer), `meshBrowse` (search/catalog), `nodeEditing`,
`data` (pivot/chart), `documents`, `analysis`, `media` (the only expo-av Video touchpoint — a
deployment without it ships no expo-av video). Same shape as Blazor, where each module registers
its own views. `src/modules/standard.ts` is the code-side twin of `default.json`;
`deployment.test.tsx` guards the two against drifting apart, and pins that the CORE pack contains
none of the domain leaves.

`deployment/examples/` holds worked examples: `acme.json` (brand + portal + an extension module),
`acme-module/` (the module shape), and `kiosk.json` (a LEAN deployment: browse + documents only —
no chat, no media). `src/deployment.test.tsx` pins that a manifest-injected leaf renders through
the composed registry.

**Where module JS will live**: these in-repo `src/modules/*` are the platform's own standard set.
A mesh module's bespoke leaves belong NEXT TO the module (its GUI pack — the same folder that
carries its Blazor views, e.g. in MeshWeaver.Plugins), referenced from a deployment manifest as a
package specifier once the plugin-packaging lane can carry JS assets; the manifest and the
composition rule here don't change when that lands — only the specifiers do.
