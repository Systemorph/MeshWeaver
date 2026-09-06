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

| route | methods | |
|---|---|---|
| `/v2/` | `GET` `HEAD` | version probe; answers the bearer challenge when unauthenticated |
| `/v2/token` | `GET` | the bearer exchange the challenge's `realm` names |
| `/v2/{name}/manifests/{reference}` | `GET` `HEAD` | tag or digest; multi-arch indexes included |
| `/v2/{name}/blobs/{digest}` | `GET` `HEAD` | streamed, range-capable, bounded by `IIoPool` |
| `/v2/{name}/tags/list` | `GET` `HEAD` | |

**Pull only.** No push, no upload, no delete — those keep going to the upstream, so this can be
switched off without a migration. Also NOT implemented: `/v2/_catalog`, the referrers API,
`tags/list` pagination, and serving a `Range` from the cache.

## The read-through cache

With `CacheDirectory` set, a miss fetches from the upstream, serves the bytes and stores them; the
next pull of that digest is served from disk **without contacting the upstream at all**.

- **Keyed by content digest, only.** A blob always; a manifest only when the reference IS a digest.
  🚨 A **tag is never cached** — a tag is mutable, so a cached one would serve yesterday's image
  forever. Every key being a content hash means there is no invalidation, no TTL and no staleness
  by construction. The price: `pull repo:tag` always needs the upstream for the tag → digest step,
  while `pull repo@sha256:…` can be served entirely from cache.
- **Every entry is verified** against the digest it is filed under, hashed as the bytes stream past.
  A mismatch is served to the caller and discarded, so the cache cannot be poisoned and a resident
  entry is provably the bytes its digest names.
- **Nothing negative is cached.** A 404 is never stored: a remembered absence would outlive the push
  that fixed it.
- **Eviction** is a least-recently-used sweep against `CacheMaxBytes`. 🚨 **This is a cache, not an
  archive** — any entry can be evicted, so it must never be treated as protection against an
  upstream purge. Its guarantee is one-directional: a hit avoids the upstream, a miss falls through
  to it, so it can only ever ADD availability.

`X-MeshWeaver-Cache` on every pull response reads `hit`, `miss`, `bypass` (not cacheable) or
`disabled`.

### Four outcomes, kept distinguishable

| situation | answer |
|---|---|
| resident in the cache | `200` + bytes, upstream **not** contacted |
| not resident, upstream has it | `200` + bytes, stored |
| genuinely absent upstream | `404`, nothing stored |
| upstream unreachable | `504` `UPSTREAM_UNAVAILABLE` |
| upstream refused the mirror's OWN credential | `502` `UPSTREAM_UNAUTHORIZED` |

🚨 **404 and 504 are never merged.** A mirror that answered "not found" when it could not reach the
registry would tell a CI job that a pinned digest had been purged while the truth was a network
blip.

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
    "ImageRoot": "Platform/Images",
    // EMPTY MEANS THE CACHE IS OFF — the mirror proxies every pull, as before.
    "CacheDirectory": "/var/lib/meshweaver/container-images",
    "CacheMaxBytes": 21474836480
  }
}
```

The mirror is **off** unless `Upstream`, `Username` and `Password` are all present: every route
answers 404 rather than serving partially. 🚨 And `CacheDirectory` set without
`AddContainerImageMirror()` fails at STARTUP naming the fix — a portal whose configuration says it
caches while it quietly proxies everything is the same half-configured failure this package refuses
elsewhere.

## Wiring

```csharp
services.AddSingleton<IContainerImageAuthenticator, MyAuthenticator>();
// 🚨 A SINGLETON: the upstream token cache is an INSTANCE field on this client, so a transient
// registration silently re-fetches a token on every request.
services.AddSingleton<UpstreamRegistryClient>();
services.AddContainerImageMirror();  // the read-through cache (a singleton for the same reason)
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
