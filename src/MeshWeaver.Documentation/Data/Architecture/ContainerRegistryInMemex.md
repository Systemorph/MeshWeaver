---
Name: A Container Registry in Memex
Category: Architecture
Description: Serving OCI images from the mesh — what it buys us that ACR cannot, the bootstrap circularity that decides the shape, and why the first increment is a read-through mirror rather than a replacement. The pull surface, the bearer handshake and closure-as-data are built; the cache, GC and the /app assembly closure are not.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="9" width="20" height="11" rx="2"/><path d="M6 9V6a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v3"/><line x1="7" y1="14" x2="7" y2="14"/><line x1="11" y1="14" x2="11" y2="14"/><line x1="15" y1="14" x2="15" y2="14"/></svg>
---

# A Container Registry in Memex

> **Status: v1 pull surface IMPLEMENTED** (`src/MeshWeaver.ContainerImages`, issue #3353) —
>
> 🚨 The assembly is **`MeshWeaver.ContainerImages`, deliberately NOT `MeshWeaver.ContainerRegistry`**
> — MeshWeaver.Plugins already ships an assembly of that name (the plugin registry module), in the
> same namespace, declaring its own `ContainerRegistryEndpoints`, `ContainerRegistryOptions` and an
> identical `MapContainerRegistry(this IEndpointRouteBuilder)`. Two assemblies of one name is the
> one-producer FATAL by name; two identical fully-qualified types is `CS0433`; two identical
> extension signatures is `CS0121`. The config section moved to `ContainerImages:` for the same
> reason. Renamed in #3361 while nothing referenced it yet — which is the only cheap moment.
> **Built:** `GET /v2/`, the bearer token exchange at `GET /v2/token`, `…/manifests/{reference}`,
> `…/blobs/{digest}` (Range included) and `…/tags/list`, proxied to the upstream with the mirror's
> own credential while the caller authenticates against memex — plus the OCI-level **closure and
> provenance of every manifest served, recorded as `ContainerImage` nodes**.
>
> 🚨 The token exchange arrived one increment LATE, and the gap is worth recording: the first cut
> emitted a correct-looking `WWW-Authenticate` challenge naming a realm at `/v2/token`, and **nothing
> served that route**. Every endpoint read correctly in isolation; a real `docker pull` went
> 401 → fetch the realm → 404 → give up. Only a test that walks the WHOLE handshake — probe,
> challenge, token, pull — could see it, which is why `PullSurfaceTest` is written as one
> conversation rather than per-endpoint assertions.
>
> **Not built:** no push, **no cache** (every pull still goes to the upstream), no GC, and no
> **/app assembly** closure — see "What v1 records" below for exactly where that line falls. The
> boot image comes from the upstream, permanently. Container images live in Azure Container
> Registry (`meshweaver.azurecr.io`), named by `ACR:` in `main-cd.yml` and referenced by eight
> workflows. This page exists so the decision is a decision rather than a recurring conversation.

## What is already in memex, and what is not

Two different things are called "the registry", and conflating them is the reason this question
keeps coming back.

| | what it serves | where it lives today |
|---|---|---|
| [Plugin Registry](../PluginRegistry) | plugin **bundles** — mesh nodes, NodeType assemblies | **memex**, re-served over a token-gated REST surface |
| container registry | OCI **images** — the portal, the migration job, the tester | **ACR** |

The bake publishes NodeType assemblies to the portals' storage shares
(`BAKE_PUBLISH_TARGETS`). That is bundles, not image layers. ACR is itself blob-backed, but that
storage is Azure's, not ours — so this is a greenfield "serve OCI from our blobs", not a
"point a new front end at layers we already hold".

## The constraint that decides the shape

**A registry that runs inside the thing it deploys cannot serve the image that boots it.**

Kubernetes pulls `memex-portal-ai` before any MeshWeaver process exists. If the registry were a
memex plugin, then starting memex would require memex to already be running. There is no ordering
that resolves this for the platform's own image.

It is also a **failure-domain merge**. Today an ACR outage and a portal outage are independent
events. A registry inside the portal makes every pod start, every scale-up and every restart depend
on the portal being healthy — a strictly higher availability bar than the portal itself carries, and
the opposite of the rule that recovery must not live in the failure domain it recovers.

> **What ACR actually is, measured** (2026-09-05), because "more available" was an assumption worth
> checking and it did not survive:
>
> | | |
> |---|---|
> | `publicNetworkAccess` | **Enabled** — internet-reachable, `defaultAction: Allow`, no IP rules |
> | `anonymousPullEnabled` | **false** — every pull needs a token |
> | `adminUserEnabled` | **false** — AAD or scoped tokens, no admin user |
> | sku | Premium |
> | `zoneRedundancy` | **Disabled** — single zone |
>
> Unauthenticated, `GET /v2/` and a manifest pull both answer **401** with
> `Bearer realm="https://meshweaver.azurecr.io/oauth2/token",service="meshweaver.azurecr.io"`.
>
> So the accurate claim is **independent failure domain**, not *higher availability*: ACR is
> single-zone today. The bootstrap argument above is unaffected — it does not depend on ACR being
> more reliable, only on it not being the thing we are trying to start. But it does mean a mirror
> could *improve* pull availability rather than merely adding a hop, which is worth measuring before
> anyone claims it either way.

**Consequence:** the boot image stays on an external registry, permanently. That is not a
limitation to engineer away; it is the correct boundary.

## What it would buy us that ACR cannot

The case is not cost or independence. It is that **an image's contents are currently opaque until
something fails to compile against them.**

1. **The closure becomes queryable data.** MeshWeaver#3328 was, at bottom, *"nobody can see what is
   in `/app`"* — the image's application directory is the reference set every satellite's modules
   compile against ([The Platform Image's Closure](../PlatformImageClosure)), and discovering its
   contents meant `docker run … ls /app`. If the registry is in the mesh, the closure is a node:
   assertable, diffable between builds, and answerable without pulling a tarball. The gate added in
   #3334 does this externally with a shell script because there is nowhere else to put it.

2. **Provenance stops being a tag-naming convention.** Today an image's origin is encoded in
   `<core-short>-p<plugins-short>` and recovered by string-splitting a tag. As mesh data it is two
   typed references.

3. **Pins become references, not copies.** `ci.yml` carries **six** literal digest copies —
   `MW_IMAGE_DIGEST` plus one `tester-image-digest:` per module-pack call, and
   `MW_PORTAL_IMAGE_DIGEST` plus one `platform-image-digest:` per call — because a reusable
   workflow's `with:` cannot read the workflow env. Half-moving that set is a live failure mode the
   staleness guard's own remedy text warns about. A registry that can answer "what is the current
   sealed set?" makes that one reference.

4. **Access control the mesh can express and ACR cannot.** Per-partition, per-user image visibility
   off `AccessContext`, rather than one registry credential shared by everything.

5. **Retention with digest-pinned protection.** An ACR purge once deleted a pinned production tag.
   Our own retention could refuse to evict a digest any live `Deployment` node names — the same
   discipline `KeepVersionsPerType` applies to assemblies, applied to layers.

## The credential argument, which is the strongest one

**Measured 2026-09-05 — registry secrets across the fleet:**

| repo | secrets |
|---|---|
| MeshWeaver | `MW_REGISTRY_KEY` |
| MeshWeaver.Plugins | `ACR_USERNAME`, `ACR_PASSWORD`, `ACR_PUSH_USERNAME`, `ACR_PUSH_PASSWORD`, `REGISTRY_PUBLISH_TOKEN` |
| MeshWeaver.Education | `MW_REGISTRY_KEY`, `MW_REGISTRY_USERNAME`, `MW_REGISTRY_PASSWORD` |
| MeshWeaver.Reinsurance | `ACR_USERNAME`, `ACR_PASSWORD`, `MW_REGISTRY_KEY` |
| MeshWeaver.SocialMedia | `ACR_USERNAME`, `ACR_PASSWORD`, `MW_REGISTRY_KEY`, `REGISTRY_PUBLISH_TOKEN` |

Two different credentials are in that table. `MW_REGISTRY_KEY` is the **memex** plugin registry —
already the npm/NuGet-style pattern where the source credential is encapsulated in the registry and
consumers hold only a registry token ([Plugin Registry](../PluginRegistry)). `ACR_USERNAME` /
`ACR_PASSWORD` are **ACR pull credentials**, handed to each satellite's lane so
`node-repo-compile-check` can `docker login` and pull the tester image.

So every satellite already holds a memex registry credential — and carries a **second, different**
credential purely to pull images. That is the sprawl, and it is the thing a mirror removes: the
satellite drops the ACR pair and reuses the token it already has.

### The bootstrap constraint does NOT apply to this use

The circularity above is about a cluster **booting its own portal**. A CI job pulling the tester
image is not a boot path — nothing is starting memex, and memex is already running. Likewise, one
memex instance serving images to a *different* installation has no circularity: only the registry
instance's own boot image must come from outside.

**The circularity is per-instance, not global.** That matters, because the credential sprawl lives
entirely in CI and in other installations — exactly where the constraint is silent.

| who pulls | today | with a mirror |
|---|---|---|
| satellite CI (tester/portal image) | ACR pull credential per repo | existing memex token |
| another installation | ACR credential it should not have | its own memex identity |
| the registry instance's own cluster, at boot | ACR | **ACR, permanently** |

## The shape: a read-through mirror first

```
   docker pull ──▶  memex /v2/…  ──▶  blob in mesh storage?
                         │                 │ hit → stream it
                         │                 │ miss → fetch from ACR, store, stream
                         ▼
                    closure + provenance recorded as mesh nodes

   docker push ──▶  ACR (unchanged)
   boot image  ──▶  ACR (unchanged, permanently)
```

Pull-side only, to begin with. Pushes keep going to ACR, so CD is unchanged and the mirror can be
turned off without a migration. Every benefit above except (4) is available from the pull side
alone, because they all derive from *reading* manifests and layers.

### What v1 actually enforces

* **Off unless configured.** `Upstream`, `Username` and `Password` must all be set, or every route
  is 404 — never a partial service, which would be indistinguishable from a working one until a
  pull returned the wrong bytes.
* **An allowlist, empty by default.** `ContainerImages:Repositories` names exactly what may be
  served. Without it, one upstream credential becomes an open read proxy for the whole registry.
* **Pull only, by construction.** A blob reference must be a content digest, so the entire upload
  family (`blobs/uploads/…`) is refused by SHAPE rather than by blocklisting names — a test caught
  that route being accepted as a pull before it shipped.
* **Repository names keep their slashes.** Kind and reference come from the END of the path, so
  `a/b/c/manifests/x` is repository `a/b/c` — what the allowlist checks is what the upstream sees.
* **Bodies stream.** `ResponseHeadersRead` upstream, `CopyToAsync` downstream; a layer never lands
  on the heap.
* **The mirror's own credential failing is a 502, not a 401** — a 401 would send the caller to fix
  a token that is not the broken thing.
* **The handshake COMPLETES.** `GET /v2/token` is the realm the challenge names, and it sits
  OUTSIDE the challenge filter: a token endpoint that answered 401-with-a-challenge would name
  itself and loop a client between the two forever. It refuses with a bare 401 instead.
* **The mirror mints nothing.** The bearer the token exchange hands back IS the caller's own
  instance key. A minted token would stay valid for its full lifetime after the key behind it was
  revoked, and would make the mirror a second issuer of credentials for an identity it does not
  own. Echoing the key means every later request re-runs the authenticator, so revocation takes
  effect at the next exchange — and the mirror stores no token state at all.
* **Layer transfers are BOUNDED, not merely unbuffered.** The body copy runs through `IIoPool`'s
  `Blob` pool (cap 128), holding a slot for the whole transfer. Not buffering keeps one layer off
  the heap; the pool is what stops a hundred concurrent pulls — a rolling restart of a large
  deployment — from each holding a socket and a buffer at once. `Http` (cap 16) would have been
  wrong: one layer parked there for a minute starves every plugin-catalog call the portal makes.

### What v1 records, and the line it does not cross

Every manifest the mirror serves is recorded as a `ContainerImage` node under
`ContainerImages:ImageRoot` — **empty means recording is off, and the mirror still proxies**. The
record carries the resolved digest (computed over the served bytes, never taken from a header), the
media type, the config digest, every layer by digest and size, an index's platforms, and the
provenance: which upstream, which repository, which reference, observed when and by whom.

**It records what it SEES, and fetches nothing speculatively.** A real pull fetches the index for a
tag and then the manifest for its own architecture, so one pull leaves both records behind, and the
index's platform entries are digests whose own records carry the layers. That keeps recording free:
no extra upstream request, no added pull latency, nothing a cache would later have to justify. The
cost is stated plainly — an index nobody ever resolves (`crane manifest` and stop) leaves its
platforms unrecorded.

Recording is **observational and cannot fail a pull**: the write is subscribed off the response
path with an explicit error arm, and a failure is a warning naming the root, never something handed
to a `docker pull`.

🚨 **The line.** This is the **OCI-level** closure — which blobs, how big, from where. It is NOT
the **/app assembly** closure that [The Platform Image's Closure](../PlatformImageClosure)
is about, and that #3328 was: those file names live INSIDE a layer tarball and no manifest names
them. Reaching them means streaming the app layer through gzip + tar and recording entry names plus
the (few-KB) `meshweaver-surface.manifest`. That is a bounded, streamable piece of work, and
deliberately a separate increment: it cannot ride the pull path — decompressing hundreds of
megabytes per pull is exactly the cost this design refuses — so it needs a trigger and a bandwidth
budget of its own.

#### How #3334's gate maps onto this data

`check-platform-reference-set.sh` has two halves, and they land on opposite sides of that line.

* **One producer** — no composed module's name may appear as `<name>.dll` in `/app` nor as an entry
  in the surface manifest. This becomes a data assertion *once the layer scan exists*: read the
  composed set from `main-cd.yml` (unchanged — it must stay the producer's own list, never a second
  copy) and assert no name appears in the image record's app-assembly list or surface manifest.
  Today the data cannot answer it.
* **The set must resolve** — this half can **never** become an assertion over data, by construction.
  It does not model MSBuild's binding rules, it RUNS them: a throwaway project whose references ARE
  the directory, built with the module lane's own flags. Data can make the probe cheaper (its inputs
  become known without pulling), but the verdict still requires a build. Saying otherwise would be
  the second-opinion mistake the script's own comments warn about.

So acceptance (2) is *"half of it, after one more increment"* — not "done", and not "impossible".

**v1 is the proxy WITHOUT the cache, deliberately.** The credential goal is met by authenticating
the caller against memex and using the mirror's credential upstream; caching is an optimisation on
top that can be added without changing the surface. Shipping it first would have meant storage,
eviction and correctness work before anything was usable.

**The first consumer to move is satellite CI, not any cluster.** It is where the duplicated
credentials are, it is unaffected by the bootstrap constraint, and a mirror that is wrong there
costs a red CI run rather than an outage. Clusters move later, if at all.

## What the implementation has to get right

**The wire protocol is small** — `GET /v2/`, manifest `GET`/`PUT`, blob `GET`, chunked blob upload
(`POST` → `PATCH` with `Content-Range` → `PUT`), `GET /v2/<name>/tags/list`. The work is not the
verb list.

- **Streaming, never buffering.** Layers are hundreds of megabytes. Every byte goes through
  `IIoPool` (`InvokeStream`), never onto a hub turn and never into memory. A registry that
  materialises a layer is a registry that OOMs the portal under a rolling restart.
- **Range requests.** Clients resume partial layer pulls; `206 Partial Content` is not optional.
- **The token dance.** A Docker client expects `401` plus
  `WWW-Authenticate: Bearer realm=…,service=…,scope=repository:<name>:pull` and then a token
  endpoint. This is where `AccessContext` plugs in, and it is the only part with real protocol
  subtlety. ACR's own challenge is a live reference for the shape we must emit.
- **A mirror does NOT remove the credential, it moves it.** Cache-fill still needs an ACR credential
  server-side, because anonymous pull is off. What changes is *who* needs one: a viewer authenticates
  to memex and is authorised by `AccessContext`, instead of holding registry credentials. That is the
  access-control gain in (4) — state it that way, not as "no credentials".
- **Content addressing is a natural fit.** Digests are immutable ids, so layer dedup across
  repositories is free if blobs are keyed by digest — the mesh is already content-addressed for
  module builds.
- **Garbage collection.** Untagged manifests and orphaned layers accumulate. Eviction must be
  refused for any digest a live deployment names.

## What would say this is working

Not "images pull". The measurable claims, with where each one actually stands:

| claim | status |
|---|---|
| the closure of a promoted image can be answered from mesh data, with no `docker run` | **OCI closure: yes** — config and every layer, by digest and size, read off the node. **/app assembly closure: not yet** — it needs the layer scan described above |
| #3334's gate can be re-expressed as an assertion over that data | **half, and the other half never** — see the mapping above: one-producer becomes data once the layer scan lands; the resolve half RUNS MSBuild and cannot be modelled |
| a pin bump moves **one** reference instead of six literals | **the data supports it** — a tag is resolved to a digest at a deterministic node path, so a consumer carries the tag. Nothing has been converted to use it yet; `ci.yml` still carries its six literals |
| an ACR outage still costs us nothing at boot | **yes, structurally** — the boot image never moved, and switching the mirror off is a configuration change |

🚨 **And one that is NOT yet measured: pull latency.** The mirror adds a hop, and no number for it
against a real upstream exists. The streaming and pool behaviour are tested (a client receives the
first bytes while the upstream is still producing the rest), but that is a correctness property, not
a latency measurement. Nothing should DEPEND on the mirror until pull latency through it is measured
against ACR directly — which is also what would settle whether the mirror *improves* pull
availability, the open question the single-zone measurement above raised.

## Related

[The Platform Image's Closure](../PlatformImageClosure) · [Plugin Registry](../PluginRegistry) ·
[Module Build Architecture](../ModuleBuildArchitecture) · [Deployment](../Deployment)
