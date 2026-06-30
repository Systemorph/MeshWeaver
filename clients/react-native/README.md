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

React Native **cannot use `@grpc/grpc-js`** (it needs Node's `http2`). For live mesh data, use **grpc-web**:
the renderer's `GrpcAreaSource` is transport-agnostic — implement its `MeshConnectionLike` over a grpc-web
client (`@improbable-eng/grpc-web` / `grpc-web`), with the portal exposing grpc-web (ASP.NET Core's
`Grpc.AspNetCore.Web` middleware on the `MeshGrpcService`, or an Envoy `grpc_web` filter). Until then,
`StaticAreaSource` drives the app from a literal area tree (as in `App.tsx`).
