---
Name: A Container Registry in Memex
Category: Architecture
Description: The fleet's own container registry at cr.meshweaver.cloud — decided 2026-09-08 as a SEPARATE service built from CNCF distribution and docker_auth (no registry code of ours), why off-the-shelf, the bootstrap note, what was measured before it shipped — plus the in-mesh pull surface, bearer handshake, closure-as-data and read-through cache that remain built.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="9" width="20" height="11" rx="2"/><path d="M6 9V6a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v3"/><line x1="7" y1="14" x2="7" y2="14"/><line x1="11" y1="14" x2="11" y2="14"/><line x1="15" y1="14" x2="15" y2="14"/></svg>
---

# A Container Registry in Memex

> **Status (2026-09-08): DECIDED — the fleet gets its OWN registry at `cr.meshweaver.cloud`, as a
> SEPARATE service.** Not a memex plugin and not a mirror in front of ACR: its own pods, its own
> host, its own blob container, built from two off-the-shelf images and zero registry code of ours
> (see "The registry as a separate service" below). Every installation — the main portal included
> — pulls its images from it, and ACR is retired afterwards. The chart half is in
> `deploy/helm/templates/registry/` (`registry.enabled`, off by default); the bootstrap note
> below is the one constraint that survives the change of shape.
>
> **Also built, and still standing:** the in-mesh v1 pull surface (`src/MeshWeaver.ContainerImages`,
> issue #3353) — `GET`/`HEAD` on `/v2/`, `…/manifests/{reference}`, `…/blobs/{digest}` (Range
> included) and `…/tags/list`, the bearer token exchange at `GET /v2/token`, the OCI-level closure
> and provenance of every manifest served recorded as `ContainerImage` nodes, and the digest-keyed
> read-through cache. It is what the closure-as-data sections below describe, and it is
> unchanged; what the decision changes is WHERE images are authoritatively stored and served.
> 🚨 The assembly is **`MeshWeaver.ContainerImages`, deliberately NOT `MeshWeaver.ContainerRegistry`**
> — MeshWeaver.Plugins already ships an assembly of that name (the plugin registry module), in the
> same namespace, declaring its own `ContainerRegistryEndpoints`, `ContainerRegistryOptions` and an
> identical `MapContainerRegistry(this IEndpointRouteBuilder)`. Two assemblies of one name is the
> one-producer FATAL by name; two identical fully-qualified types is `CS0433`; two identical
> extension signatures is `CS0121`. The config section moved to `ContainerImages:` for the same
> reason. Renamed in #3361 while nothing referenced it yet — which is the only cheap moment.
>
> 🚨 The token exchange arrived one increment LATE, and the gap is worth recording: the first cut
> emitted a correct-looking `WWW-Authenticate` challenge naming a realm at `/v2/token`, and **nothing
> served that route**. Every endpoint read correctly in isolation; a real `docker pull` went
> 401 → fetch the realm → 404 → give up. Only a test that walks the WHOLE handshake — probe,
> challenge, token, pull — could see it, which is why `PullSurfaceTest` is written as one
> conversation rather than per-endpoint assertions. The separate service was verified the same
> way, for the same reason: a real `docker login` / `push` / `pull` against the rendered configs,
> not per-endpoint reads (see below).
>
> **Not built in the mirror, and not needed once the service exists:** push, `/v2/_catalog`,
> referrers, delete, pin-protected retention. Today container images still live in Azure Container
> Registry (`meshweaver.azurecr.io`), named by `ACR:` in `main-cd.yml` and referenced by eight
> workflows; moving the producers and consumers over is the next increment, not this page's.

## The registry as a separate service

**The shape.** `cr.meshweaver.cloud` is its own Deployment pair in the portal's namespace, on its
own host, writing to its own blob container — and none of it is MeshWeaver code:

| piece | image | what it does |
|---|---|---|
| the registry | `ghcr.io/distribution/distribution:3.0.0` (CNCF distribution, the reference implementation) | serves `/v2/…`; storage driver `azure`, authenticating as the namespace's **workload identity** (`credentials.type: default_credentials`) — no storage key anywhere; token auth pointed at the auth server |
| the token server | `cesanta/docker_auth:1.14.0` | answers `https://cr.meshweaver.cloud/auth`; authenticates a caller and signs a bearer token the registry verifies against the same certificate |

One Ingress on the host routes `/auth` to docker_auth and everything else to distribution
(`proxy-body-size: 0`, request buffering off, an hour each way — a layer is one PATCH of hundreds
of megabytes). The token certificate and key, distribution's `http.secret` and the optional
notification bearer are Key Vault objects, mounted through one SecretProviderClass into both
pods. Values: `registry.*` in `deploy/helm/values.yaml`; a complete example the chart gate renders
on every pull request: `deploy/helm/values.registry.example.yaml`.

**Who may do what.** Two ways in, in the order docker_auth tries them:

1. **One static account, the publisher** (CI). Its bcrypt hash is the only credential in values —
   `htpasswd -nB publisher`, a `$2y$` hash, committable. It may `push`, `pull` and `delete`
   anything. A wrong password for THIS account is `WrongPass` and is refused immediately; it never
   falls through to the second way.
2. **Any other account name, with a MeshWeaver instance key as the password.** docker_auth's
   `ext_auth` hook (`validate.sh`, a ConfigMap) presents the password as `Authorization: Bearer`
   to `registry.validationUrl` — by default the portal's plugin catalog,
   `https://memex.meshweaver.cloud/api/plugins?ref=HEAD`. `200` authenticates, `401`/`403` denies,
   and **anything else is exit 3, an ERROR, never a pass** — a DNS failure, a timeout or a 5xx
   refuses the login and is logged by docker_auth as such, so an outage of the validator reads as
   an outage, not as "wrong key". Every authenticated account may `pull` anything.

Anonymous matches no rule and is denied by default. So an installation authenticates to the
registry with the same key it already holds for the plugin registry — the credential-sprawl
argument above, closed without the mirror.

**Why off-the-shelf.** The wire protocol is small but the operational surface is not: resumable
chunked uploads, `Range` on blobs, SAS redirects so a layer never streams through a pod, upload
purging, garbage collection, and the token handshake's every corner case. distribution has had a
decade of clients against all of it; docker_auth exists precisely to bolt an external
authenticator onto it. A registry we wrote would be a second implementation of a solved problem,
and every bug in it would be a fleet-wide pull failure. The mesh keeps what only it can provide —
the closure and provenance DATA, which the mirror records and which the registry's notification
endpoint (`registry.notifications.url`, every event POSTed with the vault-held bearer) can feed
just as well.

**🚨 The bootstrap note.** The registry's own two images are the only images an installation
pulls from OUTSIDE its own registry: `distribution` from `ghcr.io`, `docker_auth` from Docker Hub.
The constraint from "The constraint that decides the shape" is unchanged in kind and shrunk in
scope — it no longer applies to the portal's image (which the registry serves), only to the
registry's own. Both are pinned by digest in values so that boot path is reviewable.

**What was measured before this shipped** (2026-09-08, the two images run locally with the
RENDERED configs — filesystem storage instead of azure, a self-signed RSA certificate, a stub
validator — and a real `docker login`/`push`/`pull`):

* `docker login` as the publisher with the `$2y$` hash → accepted; `push` → accepted; wrong
  password → refused without reaching the validator.
* `docker login` as `instance` with the known key → accepted (the stub saw the bearer); `pull` →
  accepted; `push` → **denied by the ACL**; a bad key → refused; the validator stopped → refused,
  logged `bad return code from command: 3`; anonymous `pull` → refused.
* Every registry event reached the stub carrying the Authorization header from the vault
  (`pull` and `push` actions observed).
* 🚨 **docker_auth 1.13.0 cannot pair with distribution 3 at all.** distribution 3 keys its
  `rootcertbundle` by RFC 7638 JWK thumbprint; docker_auth's default `kid` is the legacy libtrust
  id and no `x5c` chain is sent, so EVERY token was refused with `token signed by untrusted key
  with ID` while the exchange itself answered 200 — no login succeeded, the publisher's included.
  1.14.0 adds `token.disable_legacy_key_id: true`, which the rendered config sets; that is why the
  pin is 1.14.0 and not the 1.13 the first draft named.
* distribution 3 also enables an OTLP trace exporter by default and logs a connection error every
  ten seconds without a collector; the Deployment sets `OTEL_TRACES_EXPORTER=none`.
* distribution's env override for an endpoint's `headers` map unmarshals the value as YAML into
  `[]string`, so the plain `Bearer …` the vault holds is wrapped into list form by the container
  command at start rather than stored pre-quoted.

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
   docker pull ──▶  memex /v2/…  ──▶  digest in the cache?
                         │                 │ hit  → stream it from disk, upstream NOT contacted
                         │                 │ miss → fetch from ACR, verify, store, stream
                         ▼
                    closure + provenance recorded as mesh nodes

   docker push ──▶  ACR (unchanged)
   boot image  ──▶  ACR (unchanged, permanently)
```

Pull-side only, to begin with. Pushes keep going to ACR, so CD is unchanged and the mirror can be
turned off without a migration. Every benefit above except (4) is available from the pull side
alone, because they all derive from *reading* manifests and layers.

### The pull surface, exactly

A partial surface that looks complete is worse than an obviously small one, so this is the whole
list — what answers, and what does not.

| route | methods | v1 |
|---|---|---|
| `/v2/` | `GET`, `HEAD` | **served** — the version probe, and where a client reads the challenge |
| `/v2/token` | `GET` | **served** — the realm the challenge names; `Basic` in, bearer out |
| `/v2/<name>/manifests/<tag or digest>` | `GET`, `HEAD` | **served**; cached only when the reference is a digest |
| `/v2/<name>/blobs/<digest>` | `GET`, `HEAD` | **served**, `Range` forwarded; cached on a whole-body `GET` |
| `/v2/<name>/tags/list` | `GET`, `HEAD` | **served**, never cached |

**Not implemented, deliberately** — each of these simply has no route, so it 404s rather than
half-working:

* **the entire push family** — `POST`/`PATCH`/`PUT` `blobs/uploads/…`, manifest `PUT`. Refused *by
  shape*: a blob reference must be a content digest, so `blobs/uploads/` never parses as a pull.
* **`DELETE`** of anything. The mirror owns no lifecycle.
* **`/v2/_catalog`** — repository enumeration. The allowlist already says what may be served, and a
  catalog would be a second, drifting answer to the same question.
* **the referrers API** (`/v2/<name>/referrers/<digest>`) — signatures and attestations. Nothing in
  the fleet consumes it through the mirror yet.
* **pagination on `tags/list`** (`?n=`/`?last=`) — the query string is not forwarded.
* **serving a `Range` FROM the cache.** A range request bypasses the cache entirely: a partial body
  cannot be verified against the whole body's digest, and an unverified entry is worse than none.
  It is proxied, and resumable, but not accelerated.

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

### The cache: what is stored, where, and what evicts it

`ContainerImages:CacheDirectory` turns it on; **empty means off, and the mirror then proxies every
pull exactly as it did before the cache existed.** Turning it on or off is a configuration change,
never a migration — nothing in the cache is authoritative, so discarding the whole directory costs
a re-fetch and nothing else. 🚨 A directory configured with no cache *registered*
(`services.AddContainerImageMirror()`) fails at STARTUP naming the fix, rather than quietly
proxying while the configuration claims otherwise.

**Keyed by content digest, and only by content digest.** A blob route is a digest by construction;
a manifest route is cached only when the reference IS a digest.

🚨 **A tag is never cached.** A tag is mutable, so a cached one would serve yesterday's image
forever — and the symptom would read as a stale *build* rather than a stale cache. Because every
key is a content hash, an entry can never go stale: **there is no invalidation, no TTL, and no
coherence protocol to get wrong.** The price is stated plainly rather than hidden: a `pull
repo:tag` always needs the upstream for the tag → digest step, while a `pull repo@sha256:…` — which
is what every pinned CI consumer does — can be served entirely from cache.

**Every entry is verified.** Bytes are hashed as they stream past, and an entry is filed only if the
finished hash equals the digest it would be filed under. A body that hashes to something else is
**served to the caller and discarded** — the cache cannot be poisoned by a wrong answer upstream,
and a resident entry is provably the bytes its digest names. That is what makes serving one during
an upstream outage safe rather than hopeful. Two independent mechanisms stop a truncated layer
becoming an entry: the hash cannot match, and an abandoned fill's temporary file is deleted on
dispose.

**On disk**, sharded `<CacheDirectory>/sha256/<first two hex>/<hex>`, with a one-line `.type`
sidecar carrying the media type — essential for a manifest, which a client parses by its
`Content-Type`. A fill writes to a temporary in the same directory and `File.Move`s it into place,
so a crash leaves a temporary (swept after an hour), never a truncated file that reads as complete.

**Eviction is a bounded LRU sweep.** `ContainerImages:CacheMaxBytes` (default 20 GiB) is the budget;
a sweep runs after roughly an eighth of it has been added, and evicts least-recently-*used* entries
until the directory is back under 90 % of the budget. Last-use is tracked by touching the file on a
hit, because filesystem access times are unreliable (`noatime` records none). An explicit `Sweep()`
always sweeps; only the automatic one behind a store stands down behind a sweep already running.

🚨 **The cache is NOT an archive, and this design deliberately does not claim it is one.** It is
bounded, so any entry can be evicted — including one a pinned deployment names. Its guarantee is
one-directional: **a hit avoids the upstream, a miss falls through to it, so it can only ever ADD
availability and never subtract it.** That is exactly why it is safe to ship before any retention
policy exists, and exactly why it must not be sold as protection against an upstream purge (gain
(5) above). Making it refuse to evict a digest a live `Deployment` node names is a separate,
later increment — and doing it *before* that would mean a second store that can lose a pinned
digest while looking like insurance, which is worse than no insurance at all.

**And nothing negative is cached.** A 404 is never stored: a remembered absence would outlive the
push that fixed it, and the symptom would be an image that "does not exist" long after it does.

#### Four outcomes, and they stay distinguishable

This is the part that has to be right, because the failure mode is a *confident wrong answer*.

| situation | answer | upstream contacted |
|---|---|---|
| digest resident in the cache | `200` + the bytes, `X-MeshWeaver-Cache: hit` | **no** |
| digest not resident, upstream has it | `200` + the bytes, `…: miss`, entry stored | yes |
| digest genuinely absent upstream | `404`, nothing stored | yes |
| upstream unreachable, nothing resident | `504` + `UPSTREAM_UNAVAILABLE` | attempted |
| upstream refused the *mirror's own* credential | `502` + `UPSTREAM_UNAUTHORIZED` | yes |

🚨 **404 and 504 are never merged.** A mirror that answered "not found" when it could not reach the
registry would tell a CI job that a pinned digest had been purged while the truth was a network
blip — and after this fleet's own ACR retention incident, that is precisely the wrong answer to
give confidently. The 504 body says so in words as well as in its code. `502` stays separate again:
an upstream that *answers* and refuses our credential is an operator's problem, not a network one,
and telling them apart is the difference between rotating a secret and waiting.

`X-MeshWeaver-Cache` (`hit` / `miss` / `bypass` / `disabled`) is a diagnostic, not a contract — the
tests assert the upstream's own request counter, because "served from cache" only means anything as
"the upstream was not asked", and a header cannot prove that.

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

**v1 landed in two increments, and the order mattered.** The proxy shipped first: the credential
goal is met by authenticating the caller against memex and using the mirror's credential upstream,
and that needed no storage at all. The cache came second, as an optimisation on top of an already
usable surface rather than storage, eviction and correctness work in front of one. Its surface is
additive — the routes, the handshake and the recording are unchanged, and turning the cache off
returns the mirror to exactly the shape the first increment shipped.

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
- **Content addressing is a natural fit — and v1 takes it.** Digests are immutable ids, so layer
  dedup across repositories is free: the cache keyspace is global, not per-repository, and two
  allowlisted images sharing a base layer share the entry. 🚨 That is safe *because* only bytes
  fetched through an allowlisted repository ever enter the cache, so a global keyspace grants
  nothing the allowlist does not already grant. The visible consequence is small and worth writing
  down: asking repository B for a digest only repository A holds is a cache HIT here where the
  upstream would 404 — harmless while every allowlisted repository is at the same trust level, and
  **the thing that must change first if per-repository authorization (gain (4)) is ever added.**
  The key would then have to carry the repository, at the cost of dedup.
- **Garbage collection.** Untagged manifests and orphaned layers accumulate. v1 evicts by a
  least-recently-used byte budget, which bounds the directory but protects nothing. Eviction that
  is REFUSED for any digest a live deployment names is the separate increment gain (5) describes —
  and until it exists, the cache is explicitly not a second copy of anything.

## What would say this is working

Not "images pull". The measurable claims, with where each one actually stands:

| claim | status |
|---|---|
| the closure of a promoted image can be answered from mesh data, with no `docker run` | **OCI closure: yes** — config and every layer, by digest and size, read off the node. **/app assembly closure: not yet** — it needs the layer scan described above |
| #3334's gate can be re-expressed as an assertion over that data | **half, and the other half never** — see the mapping above: one-producer becomes data once the layer scan lands; the resolve half RUNS MSBuild and cannot be modelled |
| a pin bump moves **one** reference instead of six literals | **the data supports it** — a tag is resolved to a digest at a deterministic node path, so a consumer carries the tag. Nothing has been converted to use it yet; `ci.yml` still carries its six literals |
| an ACR outage still costs us nothing at boot | **yes, structurally** — the boot image never moved, and switching the mirror off is a configuration change |
| an ACR outage is DISTINGUISHABLE from a purged digest | **yes** — `504 UPSTREAM_UNAVAILABLE` against `404`, never merged, and asserted as one experiment rather than inferred |
| a pull survives an ACR outage | **only for a resident digest, by digest** — a cache hit never contacts the upstream, so a fully-cached `repo@sha256:…` pull completes. A `repo:tag` pull cannot: the tag → digest step has no immutable key. And nothing is guaranteed resident |

🚨 **And one that is STILL not measured: pull latency.** The mirror adds a hop, and no number for it
against a real upstream exists — the cache changes the shape of that question (a hit is a local disk
read, a miss is the old hop plus a disk write) without answering it. The streaming and pool
behaviour are tested (a client receives the first bytes while the upstream is still producing the
rest), but that is a correctness property, not a latency measurement. Nothing should DEPEND on the
mirror until pull latency through it is measured against ACR directly — which is also what would
settle whether the mirror *improves* pull availability, the open question the single-zone
measurement above raised.

## Related

[The Platform Image's Closure](../PlatformImageClosure) · [Plugin Registry](../PluginRegistry) ·
[Module Build Architecture](../ModuleBuildArchitecture) · [Deployment](../Deployment)
