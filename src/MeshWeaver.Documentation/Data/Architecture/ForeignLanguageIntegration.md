# Foreign-Language & Cross-Platform Integration

How non-.NET processes — Python, Node/Bun, browsers, and native mobile apps — **join the mesh and use
mesh features natively**, and how any React-capable platform **renders mesh layout areas**. This is the
umbrella over the whole integration; the gRPC transport itself has a deeper-dive companion,
`ForeignLanguageBridge.md`.

There are two halves, and one unifying idea behind each:

1. **Talking to the mesh** — a foreign process becomes a first-class mesh participant over gRPC, because
   the mesh is **transport-agnostic**: everything crosses one seam, the `IMessageDelivery` JSON envelope.
2. **Rendering the mesh** — a foreign UI renders a mesh layout area, because the UI model is a
   **platform-agnostic JSON `UiControl` tree** — the same tree the Blazor portal and MAUI app render.

```
  Python · Node/Bun · browser · iOS/Android            .NET mesh (portal)
  ┌───────────────────────────────────┐   gRPC bidi    ┌────────────────────────┐
  │  client SDK   (transport)         │◄═══ stream ═══►│  MeshWeaver.Hosting.Grpc│
  │  @meshweaver/react (UI renderer)  │   UiControl     │  → the mesh (hubs)      │
  └───────────────────────────────────┘   tree (JSON)  └────────────────────────┘
        one tree, many leaf packs ─────────────────────── one envelope, many transports
```

---

## Part 1 — The transport (joining the mesh over gRPC)

A single gRPC **bidirectional stream IS one mesh participant connection** — the exact role
`SignalRConnectionHub` plays for the MAUI app and Blazor-WASM clients. We swap the transport skin
(SignalR → gRPC) and reuse everything else.

- **Protobuf frames, JSON carries.** `mesh.proto` only frames + streams the connection (a `connect`
  handshake, then `deliver` / `receive` frames). The message body stays the existing System.Text.Json
  `IMessageDelivery` JSON (`RawJson`, `$type`-discriminated), so the entire serialization, type-registry,
  and `AccessContext` machinery is reused unchanged and never drifts. gRPC gives typed bidi streaming and
  first-class codegen for every language from one `.proto`.
- **Server = the SignalR host, re-skinned.** `MeshWeaver.Hosting.Grpc` mirrors `MeshWeaver.Hosting.SignalR`
  almost line-for-line: `GrpcConnectionRegistry` validates the participant's bearer token, **re-stamps every
  inbound delivery's `AccessContext` with the server-resolved identity** (a client-claimed identity is never
  trusted), and registers the participant for routing. async/await lives only at the transport boundary,
  exactly as in `SignalRConnectionHub`.
- **Participant reachability.** A participant is reachable under both runtimes: `RegisterStream` (Orleans —
  the `RoutingGrain` consults `StreamRoutedAddressTypes`) AND a hosted proxy hub at the participant address
  (monolith — `RouteInMesh` short-circuits on `GetHostedHub`, the same way a Blazor circuit receives). The
  proxy's catch-all route forwards messages addressed to the participant onto its gRPC stream and leaves its
  own lifecycle messages alone.
- **Two RPC shapes.** `Open` is a single bidi stream — the natural shape for HTTP/2 clients (Node, Python,
  .NET). Browsers and React Native can't do bidi (and can't use Node's `http2`), so the service also offers a
  **gRPC-web split**: a server-streaming `Connect` (mesh→client) + a unary `Deliver` (client→mesh), enabled
  with `Grpc.AspNetCore.Web` (`app.UseMeshWeaverGrpcWeb()`). `Connect`'s ack returns a `connection_id` the
  client passes back on each `Deliver`.

The transport is proven by network-free in-memory round-trip tests (the bidi `Open` AND the `Connect`/
`Deliver` split) and a **live Kestrel (h2c) round-trip** with a real `GrpcChannel`. Full detail + diagrams:
`ForeignLanguageBridge.md`.

---

## Part 2 — The client SDKs

Each SDK is the in-language equivalent of `IMessageHub` + `MeshWeaver.AI.MeshOperations`, speaking the bidi
stream. All build on **three primitives**, and every mesh operation is a thin composition of them:

| Primitive | What it does |
|---|---|
| `observe(target, type, msg)` | request/response — send a delivery, await the reply whose `properties.RequestId` matches |
| `post(target, type, msg)` | fire-and-forget |
| `watch(target, streamId, …)` | live stream — subscribe, demux change events by `streamId` |

`get` / `search` / `patch` / `watch` are compositions of these over the existing mesh request types.

- **Python — `clients/python` (`meshweaver`)**: `grpc.aio` transport + a `Mesh` operations surface.
  ```python
  import meshweaver as mw
  mesh = await mw.Mesh.connect("https://atioz.meshweaver.cloud", token="mw_…")
  stories = await mesh.search("nodeType:Story namespace:ACME")   # mesh → python
  await mesh.patch("ACME/Stories/42", {"content": {"done": True}})  # python → mesh
  ```
- **Node / Bun — `clients/typescript` (`@meshweaver/client`)**: `@grpc/grpc-js` transport (proto loaded at
  runtime via `@grpc/proto-loader` — no codegen step), `AsyncIterable` streams, same surface.
- **Browser / React Native — `clients/grpc-web` (`@meshweaver/client-web`)**: the same `observe`/`post`/`watch`
  surface over the **gRPC-web split** (Connect-ES), for platforms that can't do the bidi `Open` (no HTTP/2
  duplex / no Node `http2`). It's a `MeshConnectionLike`, so it feeds the renderer's `GrpcAreaSource` directly.

**Security**: the bearer token travels in gRPC call metadata; the server validates it and stamps every
write with the caller's identity. A forged client-side identity is never trusted.

**Envelope shape** (`envelope.py` / `envelope.ts`) and the operation request types (marked `WIRE:`) are
pinned to the mesh's `IMessageDelivery` JSON — confirm the exact `$type`/casing against a captured sample
(the C# round-trip test emits one). Everything beneath them (transport, correlation, demux) is correct.

---

## Part 3 — The UI: rendering mesh layout areas (`@meshweaver/react`)

A MeshWeaver layout area is delivered as a **JSON `UiControl` tree** (an `{areas, data}` object, updated via
RFC 7396 merge-patches). Rendering it is: walk the tree, map each control's `$type` to a component, resolve
`/data` bindings, post click/edit events back. `clients/react` (`@meshweaver/react`) is exactly that, in
React + Fluent UI.

### The swappable-core architecture

The crux — and the direct analog of **MAUI's `MauiViewPack`**:

```
        renderer CORE   (@meshweaver/react/core — NO DOM/Fluent)
        dispatch on $type · pop skins · resolve bindings · area stream · post events
                       │  pulls components from a RegistryProvider context
          ┌────────────┴────────────┐
   Fluent DOM pack            RN pack
   @meshweaver/react       <View>/<Text>/<TextInput>
   (web · Electron · Next) (iOS · Android)
```

`ControlRenderer` and `area/*` import **no concrete component** — they pull the "leaf pack" (control + skin
components) from context. The web entry installs a Fluent DOM pack; a React Native app installs a native
pack. **Same `UiControl` tree, swappable leaves** — exactly how MAUI has a native pack and Blazor a web one.
Because the Blazor portal renders with Fluent UI Blazor, the `UiControl → Fluent React` mapping is near 1:1.

### What the pack covers

The Fluent web pack maps the full vocabulary: layout via skins (`Stack`/`LayoutGrid`/`Tabs`/`Toolbar`/
`Splitter`/`NavMenu`/`NavGroup`/`Card`), display (`Label`/`Markdown`/`Html`/`Badge`/`Icon`/`CodeSample`/
`Exception`), data (`DataGrid` + `Property`/`Template` columns, `Catalog`, `Chart`), the full input/form
family, navigation, feedback, editors (textarea — swap in Monaco), and the mesh controls. Unknown `$type`s
render a labeled fallback; extend or override by spreading into the registry.

### Data plane

The renderer depends only on an **`AreaSource`** (the `{areas,data}` tree + an event sink), so it's
transport-agnostic:

- `StaticAreaSource` — a literal tree (demos, tests, the portal sample).
- `GrpcAreaSource` — subscribes to a live area over `@meshweaver/client`, folds RFC 7396 patches into
  `{areas,data}`, and routes click/edit events back. (Layout-area protocol shapes marked `WIRE:`.)

Bindings: a control property is a literal or a `JsonPointerReference` into `/data`; form edits write back via
the binding's pointer (optimistically applied, exactly as the live stream echoes the merge-patch).

### vs MAUI / Blazor

| | UiControl tree | Leaf pack | Transport |
|---|---|---|---|
| **Blazor portal** | same | Fluent UI **Blazor** | SignalR (in-process circuit) |
| **MAUI app** | same | **`MauiViewPack`** (native) | SignalR participant |
| **`@meshweaver/react` (web/Electron/Next)** | same | **Fluent DOM** | gRPC (`GrpcAreaSource`) |
| **React Native** | same | **RN `<View>` pack** | gRPC-web (see Targets) |

---

## Part 4 — The targets

| Target | Leaf pack | Extra work | Status |
|---|---|---|---|
| **Web / Vite** | Fluent (shipped) | none | demo + 11 vitest tests, screenshot |
| **Next.js** | Fluent (shipped) | `"use client"` + Fluent SSR (~10 lines) | guide (`clients/react/docs/nextjs.md`) |
| **Electron (desktop)** | Fluent (shipped) | a `BrowserWindow` (shipped) | `clients/react/electron/main.cjs` |
| **React Native / Expo (the MAUI peer)** | **RN pack (shipped)** | Expo project + gRPC-web transport (both **shipped**) | `clients/react-native` (+ `src/live.ts`), typechecks |
| **Browser / RN live transport** | n/a (transport) | none | **`@meshweaver/client-web`** (`clients/grpc-web`), typechecks + builds |
| **Portal example** | Fluent (shipped) | an app shell (shipped) | `clients/portal`, builds, screenshot |

Next.js is the *easiest* target (React-on-the-web → same package, same Fluent pack). React Native is the
"vs MAUI" peer — same core, a native leaf pack. **Live data in a browser or React Native** uses the
**gRPC-web split** (`Connect`+`Deliver`) — `@grpc/grpc-js` is Node-only and gRPC-web can't do the bidi
`Open`. Both halves are shipped: the server (`MeshGrpcService.Connect`/`Deliver`, tested) AND the client —
**`@meshweaver/client-web`** (`clients/grpc-web`), a `MeshConnectionLike` over Connect-ES that drops straight
into `GrpcAreaSource`. The RN app wires it via `clients/react-native/src/live.ts`. Node, Electron-main, and
Next.js-server use the bidi `Open` directly via `@meshweaver/client`.

---

## Part 5 — Repo layout

```
clients/
  python/         meshweaver — Python SDK (transport + ops)
  typescript/     @meshweaver/client — Node/Bun SDK (bidi Open)
  grpc-web/       @meshweaver/client-web — browser + RN client (Connect+Deliver split)
  react/          @meshweaver/react — Fluent UI renderer (core + web pack) + GrpcAreaSource
    docs/         react-native.md · nextjs.md · demo.png
  react-native/   meshweaver-mobile — Expo app + RN leaf pack (the MAUI peer) + src/live.ts
  portal/         @meshweaver/portal-example — a web portal built from the renderer
src/MeshWeaver.Hosting.Grpc/   the gRPC mesh transport (server)
src/MeshWeaver.Documentation/Data/Architecture/ForeignLanguageBridge.md   transport deep-dive
.github/workflows/clients.yml  CI: react (typecheck+test) · client-web (typecheck) · RN (typecheck) · portal (build)
```

---

## Part 6 — Status & what needs validation

**Verified here:** the C# transport (in-memory + live Kestrel h2c round-trips), the React renderer
(pixel-verified web render, 11 vitest tests, 0.9 MB bundle), the RN connector (typechecks against real
react-native types — proving the core compiles with zero DOM/Fluent runtime), the gRPC-web client
(`@meshweaver/client-web` typechecks + builds from the canonical `mesh.proto`, wired into the RN app's
`src/live.ts`), and the portal (builds + rendered). CI typechecks/tests all of it.

**Needs your hardware / a running portal (the `WIRE:` follow-ups):**

- Pin the SDK envelope wire-shape + the layout-area subscription protocol (`SubscribeRequest` /
  `DataChangedEvent` / click-edit messages) against a live portal — capture one change + one round-trip.
- An Orleans round-trip test (the `RoutingGrain` path), complementing the monolith Kestrel test.
- A live run of `@meshweaver/client-web` against a running portal (browser + an iOS-simulator Expo run);
  the code typechecks/builds, and RN needs a streaming-`fetch` polyfill for the `Connect` server-stream.
- Widen the operation surface (move/copy/execute/threads) and the hosted-Code-node subprocess path
  (the kernel spawns `python`/`bun` for an executable Code node, reaching back through the same SDK).
