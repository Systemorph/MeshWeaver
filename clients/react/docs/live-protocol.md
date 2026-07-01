# The live layout-area protocol (verified)

How `@meshweaver/react` subscribes to a MeshWeaver **layout area** over gRPC-web and folds the change
stream into the `{areas, data}` tree the renderer consumes. Every claim here is pinned against a running
mesh (`Memex.LocalMesh`) — this is no longer "to be verified".

The whole thing is ~120 lines of client code (`src/live/grpcSource.ts` + `src/area/pointer.ts`) on top of
the transport client (`@meshweaver/client-web`, the gRPC-web split). If you only read one file, read
`grpcSource.ts` — it is the folder.

```
 browser / RN                         mesh (portal or Memex.LocalMesh)
 ┌───────────────────┐  Connect (server-stream)  ┌──────────────────────────┐
 │ MeshWebConnection │◄══════════════════════════│ MeshGrpcService.Connect   │  mesh → client frames
 │  (@meshweaver/     │──── Deliver (unary) ─────▶│ MeshGrpcService.Deliver   │  client → mesh deliveries
 │   client-web)      │                           └──────────────────────────┘
 └─────────┬─────────┘        one connection_id ties the two halves into one duplex participant
           │ watch(address, streamId, "SubscribeRequest", { reference })
           ▼
 ┌───────────────────┐   DataChangedEvent(streamId, version, change, changeType)
 │  GrpcAreaSource   │   fold each change → { areas, data } → notify React
 └───────────────────┘
```

## 1 · Subscribe

`GrpcAreaSource.start()` opens the area subscription by posting a **`SubscribeRequest`** to the hub that
owns the node, tagged with a fresh `streamId`. The one non-obvious field is the **`reference`**:

```ts
this.connection.watch(this.address, this.streamId, "SubscribeRequest", {
  reference: { $type: "LayoutAreaReference", ...this.reference },  // { area, id?, layout? }
});
```

The reference is a polymorphic `WorkspaceReference` on the wire, so it **must** carry its `$type`
discriminator (`"LayoutAreaReference"`) or the mesh can't deserialize it and no subscription is created.
Field casing is server-insensitive, so lowercase `{ area, id, layout }` passes through.

`watch` (in `@meshweaver/client-web`) posts the request via the unary `Deliver`, then yields every inbound
frame whose `streamId` matches — demuxed off the shared `Connect` server-stream.

## 2 · The change frames

The hub streams back **`DataChangedEvent`** frames:

```
DataChangedEvent { StreamId, Version, Change = RawJson { Content: "<json>" }, ChangeType, ChangedBy }
```

`ChangeType` is the fold instruction, and it is the field people get wrong:

| `ChangeType` | wire value | `Change.Content` is… | fold |
|---|---|---|---|
| **Full**  | `0` / `"Full"`  | the entire `EntityStore` object | **replace** the raw tree |
| **Patch** | anything else   | an **RFC 6902 JSON-Patch** (array of `{op,path,value}`) | **apply** the patch to the raw tree |

> ⚠️ A Patch is **RFC 6902 JSON-Patch** (op/path/value operations), **not** an RFC 7396 merge-patch.
> `DataChangedEvent.Change` is serialized as a `Json.Patch.JsonPatch` (see `DataChangedEventConverter`
> and `StandardReducers.PatchJsonElement` in `MeshWeaver.Data`). Applying it as a merge-patch corrupts the
> tree the first time an array op arrives. `pointer.ts` ships a correct immutable `applyJsonPatch`
> (array `add` inserts, `/-` appends, `replace` overwrites, `remove` splices).

A single subscription typically emits: a Full `{areas:{}, data:{progress:"Building layout…"}}`, then more
Fulls/Patches as the area's data-bound pieces resolve, converging on the real controls.

## 3 · The EntityStore shape (and the quoted keys)

`Change.Content` (Full) parses to an `EntityStore`:

```jsonc
{
  "$type": "MeshWeaver.Data.EntityStore",
  "areas": { "$type": "…", "\"Overview\"": { "$type": "…", … }, "\"$Menu:Node\"": { … } },
  "data":  { "$type": "…", "\"progress\"": { … } }
}
```

Two things bite here:

1. **`areas` and `data` are `InstanceCollection`s, not plain maps.** Each carries a leading `$type`
   collection discriminator you must skip, and the real entries follow.
2. **The entry keys are JSON-encoded.** `InstanceCollectionConverter.ConvertKeyToString` does
   `JsonSerializer.Serialize(key)`, so a string area name `Overview` is serialized as the property name
   `"\"Overview\""` — the parsed key literally *includes* the surrounding quote characters. The renderer
   keys off plain names (`areas["Overview"]`, `NamedArea.area`, `/data/<id>` pointers), so the source
   **normalizes** each collection once per change: drop `$type`, JSON-parse each key back to its plain
   string. See `normalizeStore` in `grpcSource.ts`.

The client therefore keeps **two** representations:

- `raw` — the wire `EntityStore` verbatim (quoted keys). RFC 6902 patch paths (`/areas/"$Menu:Node"/…`)
  address *this*, so patches must fold into `raw`.
- `state` — the normalized `{areas, data}` (plain keys) the renderer reads via `getState()`.

## 4 · Control `$type` — the `Control` suffix

Controls serialize with their **full class name**: `HtmlControl`, `MenuControl`, `LayoutAreaControl`,
`CollaborativeMarkdownControl`. Leaf packs register **short** names (`Html`, `Menu`, `LayoutArea`,
`CollaborativeMarkdown`) — matching the Blazor/MAUI convention. `ControlRenderer` dispatches on the exact
`$type` first, then on the **suffix-stripped** name, so both resolve. If you add a control and see
`Unsupported: FooControl`, register `Foo` in your pack (not `FooControl`).

## 5 · Server-side gotcha you may hit: the monolith proxy hub

When the mesh runs as a **monolith** (in-process, e.g. `Memex.LocalMesh`), a gRPC-web participant is
represented by a per-connection **proxy hub** whose catch-all route forwards mesh→client deliveries.
That route MUST forward **synchronously** — `HierarchicalRouting` folds route handlers inline and reads
the `Forwarded()` state immediately. An `async` forward (e.g. `ioPool.Invoke`) leaves the fold seeing an
un-forwarded delivery → it falls through to local dispatch and logs
`No handler found for delivery DataChangedEvent`, **and** the racing async pushes reorder frames on the
wire (a Full snapshot landing after a later Patch wipes fresh content). The fix
(`GrpcConnectionRegistry.ForwardToClientSync`) serializes + enqueues inline on the proxy hub's own
single-threaded action block — order preserved, no false failures. If you fork the transport, keep that
edge synchronous.

## 6 · Events back to the mesh

`GrpcAreaSource.emit` posts three shapes back over `Deliver`:

- **click** → `ClickedEvent { area, streamId }`
- **blur** → `BlurEvent { area, streamId }`
- **update** (form edit) → optimistically applies the pointer locally, then posts
  `PatchDataChangeRequest { streamId, changeType: "Patch", change }` where `change` is a sparse
  merge-patch of just the edited pointer. The server echoes the reconciled patch back on the same stream.

## 7 · Minimal wiring

```ts
import { connect } from "@meshweaver/client-web";
import { GrpcAreaSource } from "@meshweaver/react/core";

const conn = await connect(url, { token });                 // "" token ⇒ anonymous (public Doc partition)
const source = new GrpcAreaSource(conn, "Doc/Architecture", { area: "Overview" });
await source.start();                                        // folds the stream into {areas,data}
// hand `source` to <ScopeProvider source={source} area="Overview"> … <RenderArea areaKey="Overview" />
```

`clients/react-native/src/live.ts` wraps exactly this as `createLiveSource(opts)`.

## Files

| Concern | File |
|---|---|
| Subscribe + fold + emit | `clients/react/src/live/grpcSource.ts` |
| RFC 6902 patch + JSON pointer | `clients/react/src/area/pointer.ts` |
| `$type` dispatch (+ suffix strip) | `clients/react/src/render/ControlRenderer.tsx` |
| gRPC-web split transport | `clients/grpc-web/src/connection.ts` |
| Server split + proxy hub | `src/MeshWeaver.Hosting.Grpc/{MeshGrpcService,GrpcConnectionRegistry}.cs` |
