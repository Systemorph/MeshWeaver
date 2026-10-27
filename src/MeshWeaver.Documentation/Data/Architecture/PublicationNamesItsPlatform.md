---
Name: The Publication Names Its Platform
Category: Architecture
Description: A sealed upstream publication records the platform set it was baked on, and a satellite gate composes its host on exactly that set — one set identity for host and seed, so a reseal between the gate's preflight and its seed fetch can no longer decline every bundle.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2l9 5-9 5-9-5 9-5z"/><path d="M3 12l9 5 9-5"/><path d="M3 17l9 5 9-5"/></svg>
---

# The Publication Names Its Platform

Policy `publication-names-its-platform` ([Policy Not Prose](../PolicyNotProse)). Refs
MeshWeaver#7337.

## The defect

A satellite gate (`node-repo-gate.yml`, called by every repository that seeds an upstream with
`upstream-seed`) has two inputs:

1. the **platform set** its host is composed on — the caller's preflight resolution, passed in as
   digests, or the set the lane resolves itself when nothing is pinned;
2. the **seed** — the CURRENT sealed publication of each upstream for the framework identity
   (`prebuilt-bundles/<identity>/<source>/`, or the registry's
   `/api/plugins/bundles/prebuilt/<identity>/<source>`).

The identity is the compatibility key (`c<major>e<epoch>`, see
[Module Versioning](../ModuleVersioning)), one per epoch, so the publication under it is resealed
on whatever platform set the upstream last baked on, and every bundle carries that set as its
platform FLOOR (`producerPlatformVersion`). The two inputs were resolved **independently**. When the
upstream resealed on a newer set between the gate's preflight and the shard's seed fetch, every
bundle was declined — `its platform floor 3.1.11076 … is NEWER than the running platform 3.1.11069`
— and the gate failed `bake consumption` on bytes no diff touched. Measured on
MeshWeaver.Reinsurance PR #264, run 39025974143 (a "re-run failed jobs" that reused the first
attempt's pins); the same window exists on a first attempt, because publish-bake runs overlap.

## The rule

**The publication records the set it was sealed on, and the gate takes its platform from the
publication.** One set identity for host and seed, so there is nothing to race.

### The writer: `platform-set.json`

`publish-bake-bundles.sh` writes `platform-set.json` into the publication:

```json
{"identity":"c003e001","portalDigest":"sha256:…","set":"3.1.11076","testerDigest":"sha256:…"}
```

- `set` is the portal image's own `MESHWEAVER_PLATFORM_VERSION` — the value every bundle floor is
  checked against before anything is published (`assert-bake-floor.py`);
- `testerDigest` / `portalDigest` are the two images the bake composed, by digest;
- `identity` is the framework identity the bake was keyed to.

`node-repo-publish-bake.yml` passes the three values (`BAKE_PLATFORM_SET`, `BAKE_TESTER_REF`,
`BAKE_PORTAL_REF`). The record describes the bytes, so it is written only when bytes are: inside
the publication, before the sentinel, in the manifest and verified by the postcondition like every
other file, and uploaded before `architecture.txt` ([Sealed Publication Generations](../SealedPublicationGenerations)).
The sealed-skip leaves the incumbent's record in place, which is still the record of the
incumbent's bytes. It is not a content marker for the convergence verdict: two bakes of one
commit on two platforms are two publications.

It is optional, and loudly so. A lane at a ref that predates it writes none and says so; a bake that
followed a TAG writes none and warns (a record naming a tag could compose another build); a partial
record is refused before anything is published. The OCI publication carries it as a sidecar
without a change ([Plugin Bundles in the Registry](../PluginBundlesInTheRegistry)), and the
registry's publication index serves it as `platform`.

### The reader: the gate composes on the publication's set

Each gate shard reads every upstream's record **before** it composes anything
(`publication-platform.py choose`, fetched at the lane's `scripts-ref`):

| the caller passed | the publication records | the shard composes on |
|---|---|---|
| no digests | set P | **P** |
| no digests + `platform-ceiling` C | P ≤ C | **P** |
| no digests + `platform-ceiling` C | P > C | **RED**, naming P and C — a pull request does not run on a set `main` has not passed, and composing on C would decline every bundle floored above it |
| pinned digests = P | set P | P |
| pinned set older than P | set P | **RED**, naming both — this is the declined case, named before anything is composed |
| pinned set newer than P | set P | the pin, with a warning naming both (transitional, below) |
| anything | no record | its own platform, with a warning that the race stays open for that publication |

Two upstreams sealed on different platforms are RED naming each: one host cannot be both.

**The address comes before the host.** The publication is found by framework identity, and the
host that would report it does not exist yet. The identity is the compatibility key, so it is
computed from the set name (the major) and core's declaration
`src/MeshWeaver.Compiler/platform-compatibility.json` (the current epoch, and each declared break's
`previousEpochCeiling` for the sets before it). The seed step then asserts that the composed host
resolves the same key; a disagreement is RED by name, never a seed read for another identity.

**The seed re-checks.** When the seed step reads each publication (`publication-platform.py verify`,
on every read including a re-read after a reseal mid-fetch), the record must still name the host
the shard composed. A reseal on another set in the minutes between is RED naming both — a re-run
reads the publication that then applies — instead of a wall of declined bundles.

### What a caller passes

- **To take the publication's set:** no digests. On a pull request, `platform-ceiling:` the
  preflight's `set` (the set the repository's `main` has passed); on `main` nothing. With a ceiling
  the lane resolves exactly that set (the freeze path's selection), so even a publication without a
  record composes on the ceiling.
- **To pin:** digests and `platform-set` as before. A pin is checked against the record, never
  silently replaced.
- `platform-ceiling` beside pinned digests is refused: a pin and a ceiling are two answers to one
  question.

### Transitional: a pin newer than the publication

Every satellite that seeds an upstream pins its preflight's resolution today, and that set is almost
always newer than the set the upstream last baked on. Such a pin adopts every bundle (each floor is
at or below it), so it composes as pinned, with a warning naming both sets. It turns RED once those
callers pass no digests (and `platform-ceiling` on a pull request). A pin OLDER than the
publication's set is RED from the start: that is exactly the declined case.

## What this does not cover

- The CI platform volume (`/opt/platform`) keeps the newest sealed sets and each subscribed
  repository's main-passed set. A publication sealed on a set the volume no longer holds is RED on
  a volume runner ("moved PAST it") until the refresh also installs the sets the upstream
  publications name; a container-mode runner pulls it by digest.
- Bundles carried forward from an earlier publication (a withheld package) were floored by the run
  that baked them, at or below that run's set. The record names the set of the run that sealed the
  publication; the seal-time floor gate checks only the bundles that run baked.
- The module set the gate composes (`registry-modules`) is read from the same publication by
  identity; on the registry route it is not pinned to the generation the seed read.

## Where it lives

| piece | file |
|---|---|
| the record's writer | `.github/scripts/publish-bake-bundles.sh` (THE PLATFORM RECORD) |
| the values the lane passes | `.github/workflows/node-repo-publish-bake.yml`, step `Publish bundles to every portal target` |
| the writer's cases | `.github/scripts/test-publish-bake-overlap.py` ("platform record") |
| the registry index field | MeshWeaver.Plugins `PluginBundleEndpoints.PrebuiltIndex` |
| the gate's decision and re-check | `.github/scripts/publication-platform.py`, `node-repo-gate.yml` step `publication-platform` and the seed step |
