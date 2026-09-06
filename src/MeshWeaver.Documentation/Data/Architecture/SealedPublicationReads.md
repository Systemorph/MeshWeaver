---
nodeType: Markdown
name: Sealed Publication Reads
category: Architecture
description: How a consumer reads a sealed bundle publication while the publisher is replacing it — the three answers (404, 503, 412), the generation that pins one publication instance, and what is still not closed.
icon: /static/NodeTypeIcons/box.svg
---

# Sealed Publication Reads

A **sealed publication** is one producing repo's baked content for one framework identity, on the
shared storage every portal mounts and the registry serves:

```
<root>/_releases/<platform-version>       → that release's framework identity
<root>/<identity>/<source>/<bundle>.zip   → the NodeType bundles
<root>/<identity>/<source>/modules/…      → the module bundles the bake composed, + _index
<root>/<identity>/<source>/_complete      → the seal, written strictly LAST
```

[CI Content Bake](../CiContentBake) covers what produces it and what boot does with it. This page is
about the other half: **reading one while it is being replaced.** That is not an edge case — it is
the normal state of the `plugins` prefix for a minute and a half at a time, several times an hour
during a release.

## The window is deliberate, and it cannot be closed in this layout

`publish-bake-bundles.sh` replaces a publication in place, and its first act is to **remove the
seal**:

```bash
# Readers must never seed a mid-replace mix of old and new bundles under a stale sentinel
az storage file delete ... --path "$dest/_complete"
... upload bundles, modules, markers ...
# LAST write — the atomic completeness marker.
az storage file upload ... --source "$SENTINEL_LOCAL"
```

Unsealing first is correct: it is what makes "sealed" mean "every listed bundle is here", so a
reader can never be handed a mix of old and new bundles under a stale sentinel. The cost is a window
in which the directory is deliberately unreadable, and it is **per target** — `BAKE_PUBLISH_TARGETS`
names several shares and the publisher walks them in order, so each gets its own.

Measured on 2026-09-06 (MeshWeaver.Plugins run 34029118937, one publish, two targets):

```
11:56:42  → target 1: prebuilt-bundles/sa55f8190…/plugins (36 bundle(s), 4 module(s))
11:58:11  sealed: target 1/_complete (36 bundle(s))
11:58:18  → target 2: prebuilt-bundles/sa55f8190…/plugins        ← unsealed here
11:58:24  a satellite gate's read lands, 6 seconds in            ← MeshWeaver#3401
11:59:47  sealed: target 2/_complete (36 bundle(s))
```

🚨 **And the prefix has TWO writers.** Core CD's `plugins-bake` job and the MeshWeaver.Plugins
satellite's own `publish-bake` both publish `bake-source: plugins`, so both write
`prebuilt-bundles/<identity>/plugins/`. Attributing a window to "core CD is rolling" is therefore
wrong roughly half the time — the 2026-09-06 red was the satellite's own bake, while two core CD
runs were in flight publishing a *different* identity.

## Three answers where there used to be one

`PublishedBundleCatalogue.SealedPublicationOf` reads the seal and returns either the listing plus a
**generation**, or a reason. The prebuilt routes turn that into three distinct statuses, and the
distinction is the whole point — before it, all three were `404`:

| status | meaning | what a consumer does |
|---|---|---|
| `404` | the seal does not list this name | refuse — a permanent mistake |
| `503` + `Retry-After` | the publication exists and is **being replaced right now** | wait it out; it is self-healing |
| `412` | the caller pinned a generation and the publication has since moved | **re-read the publication that now applies** |

🚨 **A `404` for a name the index just listed used to be the *only* signal for all of this, and it
named the wrong thing.** The route re-evaluates the seal on every request, so a `404` on
`Export.zip` was equally consistent with *some other* bundle having gone absent a moment earlier —
the name in the error was where the fetch loop happened to be, not what broke. That is what cost the
original #3401 investigation its first two rounds.

## The generation: one publication INSTANCE

A consumer does **N+1 reads** — the index, then each bundle it names — of a directory that can be
resealed underneath it. Statuses alone cannot express that: each individual read is served from a
complete, self-consistent seal, and only the *set* is torn.

So the index carries a `generation` (also the `ETag`), and a bundle fetch pins it with `If-Match`:

```
GET …/prebuilt/<identity>/plugins            → {"bundles":[…], "generation":"a1b2c3d4"}
GET …/prebuilt/<identity>/plugins/Export.zip    If-Match: a1b2c3d4
```

The token folds the seal's **listing** together with the **instant the seal was written**, so a
reseal to an identical bundle list is still a different generation — which it is, because the bytes
behind those names may have changed. It is **opt-in**: a registry that predates it publishes no
generation, the consumer then sends no `If-Match`, and behaviour is unchanged. That is what keeps a
satellite on an older portal working.

## The consumer contract

Every consumer of a sealed publication obeys the same four rules.

1. **Read the index, then pin what it said.** Every bundle fetch carries the generation its listing
   came from.
2. **On `503`, wait** — the existing `15 30 60 90` backoff. This is not a retry papering over a
   fault: `503 + Retry-After` is the server saying "this exists and is being replaced", and waiting
   is what that status means.
3. **On `412`, read the publication that NOW applies** — re-read the index and start the fetch over,
   at most `RESTARTS` (2) times, then go **RED naming #3401**. The bound is what keeps this a read
   rather than a poll: a publisher resealing faster than a read completes is a real condition, and
   it must be reported, not absorbed.
4. **Discard the half you hold.** A bundle from the superseded publication is deleted before the
   re-read. Without this, a name the rejected seal listed and the new one does not would linger in
   the seed — a bundle from a publication the consumer refused, which is exactly the mix the
   precondition exists to prevent.

🚨 **Rules 3 and 4 are a pair.** Detecting the move and then giving up trades a *silent* mix for a
*guaranteed red* on every overlapping publish — a satellite's pin-move PR going red for a defect
that is not in it. Detecting the move and continuing on the half already fetched is worse still.

### The three consumers

| consumer | reads | lives in |
|---|---|---|
| `node-repo-gate.yml`, the upstream seed | the bundle index + each bundle | this repo, pinned by each satellite's `uses:` |
| `.github/scripts/compose-sealed-modules.sh` | the module-set index + each module | this repo, fetched at each satellite's `platform-ref` |
| `memex build plugin` (`BuildPluginCommand`) | both | `src/MeshWeaver.Cli` |

The `registry_get` backoff is **duplicated on purpose** between the workflow and the script: the
workflow is pinned by the caller's `uses:` while the script is fetched at `platform-ref`, so a
shared file would be a third independently-pinned artefact — the skew that once let an old workflow
drive a new script and seal an empty module set. **Change one, change both.**

### Why the module lane matters most

A mixed *bundle* seed usually fails loudly at compile. A mixed **module set** does not: module bytes
from two publications are the mvid mismatch that makes the boot seeder DECLINE every NodeType
assembly built against them —

```
dependency record mismatch — built against mvid:…, live is mvid:…
```

— which is invisible in CI and surfaces only when a portal boots and renders nothing. That is the
failure `assert-bake-consumption.sh` exists to catch, and it is why the module lane is pinned even
though its window is the same size as the bundle lane's.

## What is NOT closed

Stated plainly, because a page that only lists what works is how the next session repeats this.

- 🚨 **Two writers on one prefix can produce a seal whose bytes are a mix, and no reader can detect
  it.** If core CD and the Plugins satellite overlap on `<identity>/plugins`, both unseal, both
  upload, and the last to seal writes a sentinel over a directory holding some of each one's bytes.
  The result is *one* seal with *one* generation — self-consistent to every consumer, and wrong.
  Closing it needs either a single owner for the prefix or a publish that cannot interleave
  (a generation directory plus an atomic pointer swap); both are scope calls, not code changes.
- **The window itself remains.** In this layout it cannot be removed — in-place replacement means
  unsealed time, and the alternative is a layout migration every reader must land first (the portal
  boot seeder, the gate's Azure-direct path, and every pinned satellite workflow copy).
- **The Azure-direct read path carries no generation.** `az storage file download-batch` against the
  share has no server to ask, so `--storage-target` consumers still do unpinned N+1 reads. The lanes
  all pass `--registry-url`; the storage path is the OIDC fallback.

## Verification

Both harnesses provoke the interleave deterministically rather than racing it, and both fail when
the pin is removed:

- `test/Memex.Portal.Shared.Test/SealedModuleCompositionTest.cs` — the REAL registry endpoints in a
  TestServer, the REAL `memex build plugin` composition against them, and a publisher that reseals
  between the first module fetch and the second. Unpinned, it composes `MeshWeaver.AI` from
  generation 1 and `MeshWeaver.Essentials` from generation 2 and says nothing.
- `.github/scripts/test-sealed-module-compose.py` — runs the REAL `compose-sealed-modules.sh`
  against a stub registry that republishes mid-composition. Four cases: settled, resealed once,
  reseal storm, and a registry publishing no generation at all. It asserts `If-Match` was actually
  sent, because a script that stopped pinning would still compose two files and pass on the outputs
  alone.

Related: [CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch) · [Module Build Architecture](../ModuleBuildArchitecture)
