---
Name: A Container Registry in Memex
Category: Architecture
Description: A design for serving OCI images from the mesh — what it would buy us that ACR cannot, the bootstrap circularity that decides the shape, and why the first increment is a read-through mirror rather than a replacement. Proposal, not built.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="9" width="20" height="11" rx="2"/><path d="M6 9V6a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v3"/><line x1="7" y1="14" x2="7" y2="14"/><line x1="11" y1="14" x2="11" y2="14"/><line x1="15" y1="14" x2="15" y2="14"/></svg>
---

# A Container Registry in Memex

> **Status: PROPOSAL.** Nothing described here is built. Container images live in Azure Container
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
events. A registry inside the portal makes every pod start, every scale-up and every restart
depend on the portal being healthy — a strictly higher availability bar than the portal itself
carries, and the opposite of the rule that recovery must not live in the failure domain it
recovers.

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
  subtlety.
- **Content addressing is a natural fit.** Digests are immutable ids, so layer dedup across
  repositories is free if blobs are keyed by digest — the mesh is already content-addressed for
  module builds.
- **Garbage collection.** Untagged manifests and orphaned layers accumulate. Eviction must be
  refused for any digest a live deployment names.

## What would say this is working

Not "images pull". The measurable claims are:

* the closure of a promoted image can be answered from mesh data, with no `docker run`;
* #3334's gate can be re-expressed as an assertion over that data;
* a pin bump moves **one** reference instead of six literals;
* an ACR outage still costs us nothing at boot, because the boot image never moved.

## Related

[The Platform Image's Closure](../PlatformImageClosure) · [Plugin Registry](../PluginRegistry) ·
[Module Build Architecture](../ModuleBuildArchitecture) · [Deployment](../Deployment)
