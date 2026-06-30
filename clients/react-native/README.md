# MeshWeaver on React Native (Expo) — the MAUI peer

A native leaf pack (`src/rnPack.tsx`) over the Fluent-free renderer core (`@meshweaver/react/core`).
Same `UiControl` tree the Blazor portal and MAUI render — `<View>`/`<Text>`/`<TextInput>` leaves instead
of Fluent DOM. This is the direct analog of MAUI's native `MauiViewPack`: the core (dispatch, binding,
skins, area stream) is shared; only the leaf components change.

```tsx
// App.tsx
<RegistryProvider pack={rnPack}>
  <ScopeProvider source={source} area="main">
    <RenderArea areaKey="main" />
  </ScopeProvider>
</RegistryProvider>
```

## Run

```bash
# scaffold deps, link the renderer, run on the iOS simulator
npm install
npm install @meshweaver/react        # or a workspace / file:../react link for the core
npm run ios                          # expo start --ios  (press i)
```

In a monorepo, point Metro at `../react` (Metro `watchFolders` + a `@meshweaver/react` alias), or install
the published package. `npm run typecheck` checks the pack against the core via a tsconfig path.

## Leaf pack coverage

Stacks/grids/cards/nav/toolbar (skins → `View`), `Label`/`Markdown`/`Html`/`Badge` (`Text`), `Button`
(`Pressable`), `TextField`/`TextArea` (`TextInput`), `CheckBox`/`Switch` (`Switch`), `DataGrid`/`Catalog`
(scrollable rows), `Progress`/`Spinner` (`ActivityIndicator`). Unknown `$type`s render a labeled fallback.
Grow it exactly like the Fluent pack — same `LeafPack` shape.

## Live transport

React Native **cannot use `@grpc/grpc-js`** (Node `http2`), and **gRPC-web cannot do bidirectional
streaming** — so the mesh transport exposes a **gRPC-web split**: a server-streaming `Connect` (mesh→client)
plus a unary `Deliver` (client→mesh). **The server side is shipped** — `MeshGrpcService.Connect`/`Deliver` +
`Grpc.AspNetCore.Web` (call `app.UseMeshWeaverGrpcWeb()`); `Connect`'s ack returns a `connection_id` the
client passes to each `Deliver`.

**The client side is shipped too** — [`@meshweaver/client-web`](../grpc-web) implements `GrpcAreaSource`'s
`MeshConnectionLike` over Connect-ES (`@connectrpc/connect-web`): `Connect` feeds the receive stream (demux
by `streamId`), `Deliver` sends each delivery. Wire it in with [`src/live.ts`](src/live.ts) — fill in the
`LIVE` config in `App.tsx` and the app renders a real portal layout area instead of the bundled sample:

```ts
// App.tsx
const LIVE: LiveOptions = { url: "https://atioz.meshweaver.cloud", token: "mw_…", address: "@app/Home", area: "main" };
```

`createLiveSource` connects over gRPC-web and feeds a `GrpcAreaSource`; left `null`, `StaticAreaSource` drives
the app offline from a literal area tree. The same client unblocks live data in a **browser** web app too
(`@meshweaver/client` is Node-only). One runtime caveat on RN: gRPC-web server-streaming needs a streaming
`fetch` — install a streaming-fetch polyfill before `connect()` (see the client's README).
