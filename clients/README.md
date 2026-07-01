# MeshWeaver clients

Foreign-language and cross-platform clients that **join the mesh** (transport) and **render mesh layout
areas** (UI) — the same `UiControl` tree the Blazor portal and MAUI app render, reached over gRPC instead
of SignalR.

Full architecture: `src/MeshWeaver.Documentation/Data/Architecture/ForeignLanguageIntegration.md`
(transport deep-dive: `ForeignLanguageBridge.md`).

## Packages

| Path | Package | What it is |
|---|---|---|
| [`python/`](python/) | `meshweaver` | Python SDK — `grpc.aio` transport + mesh operations |
| [`typescript/`](typescript/) | `@meshweaver/client` | Node/Bun SDK — `@grpc/grpc-js`, bidi `Open`, `AsyncIterable` streams |
| [`grpc-web/`](grpc-web/) | `@meshweaver/client-web` | Browser + React Native client — gRPC-web `Connect`+`Deliver` split (Connect-ES) + `Mesh` ops |
| [`react/`](react/) | `@meshweaver/react` | Fluent UI renderer — swappable core + web pack + `GrpcAreaSource` |
| [`react-native/`](react-native/) | `meshweaver-mobile` | Expo app + native leaf pack — the MAUI peer (live via `client-web`) |
| [`portal/`](portal/) | `@meshweaver/portal-example` | a web portal built from the renderer |

## The two ideas

- **Transport-agnostic mesh.** A foreign process is a first-class participant over a gRPC bidi stream — the
  same role MAUI/SignalR play. Protobuf frames the connection; the existing `IMessageDelivery` JSON carries
  the message, so the whole serialization/identity stack is reused.
- **Platform-agnostic UI.** A layout area is a JSON `UiControl` tree. The React renderer's core
  (`@meshweaver/react/core`) dispatches/binds/lays-out with **no concrete component**, pulling a "leaf pack"
  from context. Web/Electron/Next.js use the Fluent DOM pack; React Native uses a `<View>` pack — the direct
  analog of MAUI's `MauiViewPack` vs Blazor's web renderers.

## CI

`.github/workflows/clients.yml` runs on any PR touching `clients/`: `meshweaver` (Python) pytest,
`@meshweaver/react` typecheck + vitest, `@meshweaver/client` (Node) typecheck, `@meshweaver/client-web`
typecheck + vitest (buf-generates from the canonical `mesh.proto`), the RN connector typecheck, and the
portal build. Both gRPC-codegen jobs generate from the one canonical `mesh.proto`.
