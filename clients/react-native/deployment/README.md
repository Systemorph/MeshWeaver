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

`deployment/examples/` holds a complete worked example: `acme.json` (brand + portal + one module)
and `acme-module/` (the module shape). `src/deployment.test.tsx` pins that a manifest-injected
leaf renders through the composed registry.
