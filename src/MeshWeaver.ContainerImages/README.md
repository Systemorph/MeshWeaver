# MeshWeaver.ContainerImages

Serves the **OCI Distribution pull surface** from a MeshWeaver portal, proxying container images
from an upstream registry with one credential the portal holds — while each caller authenticates
against the mesh.

## Why

Every satellite repository carried an upstream registry username/password purely so its CI could
`docker login` and pull the platform tester image — *beside* the mesh instance key it already held
for plugin bundles. Two credentials, one job, in every repository. This collapses that to one.

It is the same encapsulation the plugin registry already applies: the source credential lives in the
registry, and consumers present a registry token.

## What it serves

| route | |
|---|---|
| `GET /v2/` | version probe; answers the bearer challenge when unauthenticated |
| `GET /v2/token` | the bearer exchange the challenge's `realm` names |
| `GET /v2/{name}/manifests/{reference}` | tag or digest; multi-arch indexes included |
| `GET /v2/{name}/blobs/{digest}` | streamed, range-capable, bounded by `IIoPool` |
| `GET /v2/{name}/tags/list` | |

**Pull only.** No push, no upload, no delete — those keep going to the upstream, so this can be
switched off without a migration. There is **no cache**: every pull reaches the upstream.

### The token exchange

A client probes `/v2/`, is answered `401` with
`WWW-Authenticate: Bearer realm="…/v2/token",service="…"`, fetches that realm with
`Basic base64(user:key)`, and presents the result as a bearer on every later request.

🚨 The mirror **mints nothing**: the bearer it returns IS the caller's own key. A minted token would
outlive the key's revocation; echoing it means every request re-runs the authenticator, and the
mirror holds no token state. `/v2/token` therefore refuses with a BARE 401 — a challenge there would
name itself and loop the client.

## What it records

With `ImageRoot` set, every manifest served is written as a `ContainerImage` node: the resolved
digest (computed over the served bytes, never taken from a header), media type, config digest, every
layer by digest and size, an index's platforms, and the provenance — which upstream, which
repository, which reference, observed when and by whom. Register the node type with
`builder.AddContainerImages()`.

The mirror records what it **sees** and never fetches speculatively, so recording costs no extra
upstream request and adds no pull latency. It is observational: a failed write is a warning, never a
failed pull.

🚨 This is the **OCI-level** closure — which blobs, from where. It is *not* the `/app` assembly list;
those names live inside a layer tarball and reaching them is a separate increment.

## Configuration

```jsonc
{
  "ContainerImages": {
    "Upstream": "myregistry.azurecr.io",
    "Username": "<pull credential>",
    "Password": "<pull credential>",
    // EMPTY MEANS NONE. Without this, one upstream credential becomes an
    // open read proxy for the entire registry.
    "Repositories": [ "memex-portal-ai", "mw-plugin-test" ],
    // Where observed images are recorded. The node must already exist.
    // EMPTY MEANS RECORDING IS OFF — the mirror still proxies normally.
    "ImageRoot": "Platform/Images"
  }
}
```

The mirror is **off** unless `Upstream`, `Username` and `Password` are all present: every route
answers 404 rather than serving partially.

## Wiring

```csharp
services.AddSingleton<IContainerImageAuthenticator, MyAuthenticator>();
// 🚨 A SINGLETON: the upstream token cache is an INSTANCE field on this client, so a transient
// registration silently re-fetches a token on every request.
services.AddSingleton<UpstreamRegistryClient>();
services.Configure<ContainerImageOptions>(config.GetSection(ContainerImageOptions.SectionName));

meshBuilder.AddContainerImages();   // the ContainerImage node type (recording half)
app.MapContainerImages();           // the pull surface
```

`IContainerImageAuthenticator` is a seam: this package takes no dependency on any particular
identity system, so a host binds it to whatever already issues its tokens. It speaks ONE shape —
`Bearer <key>` — because the mirror normalises `Basic` to it first; an implementation must not grow
a second code path for `Basic`.

## The one hard limit

**A portal cannot serve the image that boots it.** That pull happens before any MeshWeaver process
exists, so a cluster's own boot image must come from the upstream registry directly. Serving CI, and
serving *other* installations, has no such circularity — the constraint is per-instance, not global.

Design and rationale: `Doc/Architecture/ContainerRegistryInMemex`.
