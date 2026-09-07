---
Name: Sealed Publication Generations
Category: Architecture
Description: The layout that makes a mixed bundle publication unrepresentable — a generation directory per publication plus a one-line pointer swapped last — the reader contract, the retention rule, and the ordered migration that gets there without a window in which some readers use the pointer and some do not.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7v10a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-6l-2-2H5a2 2 0 0 0-2 2z"/><path d="M8 13h8"/><path d="M8 17h5"/></svg>
---

# Sealed Publication Generations

[Sealed Publication Reads](../SealedPublicationReads) describes how a publication is written and read
today, and ends with what is **not** closed. This page is that remainder: the layout that closes it,
what each reader must do, and the order the migration has to land in.

It is a design plus a landed first phase, not a finished migration. Every section says which phase
it belongs to, and ["Where this stands"](#where-this-stands) says exactly what is live.

## The defect this removes

A publication is replaced **in place**. `publish-bake-bundles.sh` deletes `_complete`, uploads over
the live files, and re-seals — deliberately, because unsealing first is what makes "sealed" mean
"every listed bundle is here".

Interleave two publishers on one prefix and the sealed-skip's answer is stale for whichever loses
the race: both unseal, both upload, and the last to seal writes a sentinel over a directory holding
**some of each one's bytes**. That is one seal with one generation — self-consistent to every
consumer, and wrong. The read-side generation cannot see it (there is nothing stale to refuse), the
boot seeder cannot see it (the sentinel is present and every listed bundle exists), and the first
symptom is `dependency record mismatch — built against mvid:…, live is mvid:…` on a portal that
renders nothing.

🚨 **The publisher's byte-level postcondition (#3496) turns that from a silent mix into a loud
refusal, and it is not the same thing as preventing one.** It re-reads every uploaded file's digest
immediately before the seal and refuses when any is foreign. What it cannot cover is the interval
between that last read and the `_complete` upload: a publisher overwriting a file inside that single
write still lands under this run's seal. The exposure drops from a whole ~90-second publication to
one file upload — a smaller window, still a window. **A postcondition is not mutual exclusion.**

🚨 **And "give the prefix one owner" is not the answer either — that was measured.** Of the overlaps
actually observed on 2026-09-06, *zero* were between the two lanes and **four** were two runs of the
*same* lane on one identity. A rule about which repository owns a prefix does not address a lane
racing itself. The numbers are in
[Sealed Publication Reads](../SealedPublicationReads) → "How many writers, measured".

## The layout

Each publication is written into its **own directory**, and a one-line pointer says which one
applies:

```
<root>/<identity>/<source>/_current                  the pointer: one line, a bare directory name
<root>/<identity>/<source>/<generation>/…            one publication INSTANCE
<root>/<identity>/<source>/<generation>/_complete    its seal, still written strictly last
<root>/<identity>/<source>/<generation>/modules/…    its module set, + _index
```

`<generation>` is the publisher's **publication token** — `<repository>-<run id>-<attempt>`, already
minted by `publish-bake-bundles.sh` and already restricted to `[A-Za-z0-9._-]` so it is a legal bare
name on every backend. It is unique per run by construction.

That one property is the whole design:

- **Two publishers never write the same bytes.** Disjoint directories, so there is nothing to
  interleave. A mix becomes **unrepresentable**, not merely detectable.
- **Publishing ends with one small write.** A reader either sees the previous generation — intact,
  sealed, still on the shelf — or the new one.
- **A reader that resolved early is not torn.** Its generation is still there; the pointer moving
  does not delete it.

### No pointer means the flat layout

A source directory with **no** `_current` **is** its own publication directory. That is exactly
today's behaviour, it stays legal for as long as any reader needs it, and it is what makes the
migration below possible at all. Resolution is opt-in **by the writer**, never by the reader.

### What is still a window, stated honestly

The pointer is one small file, and writing it is not atomic on every backend: `az storage file
upload` is a create-then-put-range, so a reader can catch it empty or short.

**That does not produce a mix.** An unreadable, empty, escaping or dangling pointer resolves to the
source directory — see the resolution rules below — so the worst case is:

| phase | a torn pointer read gives | what the reader does |
|---|---|---|
| flat copy still present | the previous publication, whole | serves it; correct, just not the newest |
| flat copy gone | a directory with no `_complete` | "being republished right now" → `503` + `Retry-After`, which every consumer already waits out |

So the trade is: **~90 seconds in which a mix can be sealed** becomes **the duration of one small
file write in which a reader may be told to come back**. The failure *mode* changes, not only its
size — from "a sealed mix nobody can detect" to "read it again". Where the backend offers an atomic
rename (Azure Files' `Rename File`, present in the REST API since 2021-04-10 though not exposed by
every `az storage file` build), phase 3 should publish the pointer that way and remove even that.

## The reader contract

One function resolves the pointer, and **every path is composed under its result**:

```csharp
ShippedPrebuiltBundles.PublicationDirectoryOf(sourceDirectory, logger)
```

🚨 **The one way to get this wrong is silent, so the API is shaped against it.** A reader that
resolves the pointer for the *listing* and then composes its file paths under the **source**
directory serves the flat publication's bytes under the generation's token — the very mix the
generation exists to prevent, wearing a token that says it is not. During the migration both paths
exist, so it is a wrong answer rather than a missing one. That is why `SealedPublicationOf` and
`SealedModulesOf` **hand the resolved directory back** on the reading (`Directory`) instead of
leaving each caller to resolve a second time.

**Resolution rules.** It never throws; it falls back. Each of these resolves to the source
directory:

| pointer | why it falls back |
|---|---|
| absent | the flat layout — the normal state today |
| empty or blank | being replaced right now; the previous generation still applies |
| unreadable (`IOException`) | same, mid-write |
| `.`, `..`, anything with a separator, anything rooted | 🚨 a pointer is a NAME. It must never be able to address bytes outside its own source directory — refused and logged as a warning, because a publisher wrote something this reader will not follow |
| names a directory that is not on disk | a retention sweep outran the pointer; logged as a warning |

### Retention

A pointer swap must never delete what a reader is mid-way through: a consumer doing N+1 reads may
have resolved the previous generation seconds ago.

- Never delete the generation `_current` names.
- Keep the previous generation for at least as long as the slowest consumer's N+1 read plus its
  backoff. The `503` ladder is `15 30 60 90`, so **24 hours** is a bound with three orders of
  magnitude of headroom and no measurable storage cost at ~43 files per publication.
- The sweep is the **publisher's**, at the end of its own run, after the pointer moves. Nothing else
  knows when a publication stopped applying.
- 🚨 A retention sweep is not a garbage collector to be tuned down when the share fills. Deleting a
  generation a reader still holds re-creates a torn read — which is the state this layout exists to
  make impossible.

## The migration, in order

The order is forced by **who reads what, and who pins whom**.

The critical fact is that the HTTP consumers do **not** read the layout at all — the registry
resolves it for them. So a satellite's pinned copy of `compose-sealed-modules.sh` or
`node-repo-gate.yml`, however old, needs no change as long as it goes through `--registry-url`.
Only the Azure-**direct** path reads the share itself.

| reader | reaches the layout | pinned by |
|---|---|---|
| `ShippedPrebuiltBundles.SeedPublishedRoot` (portal boot) | directly, on the mounted share | the portal IMAGE — rolls continuously, pins nothing |
| `PublishedBundleCatalogue` + the registry's prebuilt routes | directly, server-side | the portal IMAGE |
| `compose-sealed-modules.sh`, `node-repo-gate.yml` (registry path), `memex build plugin` | over HTTP — **the server resolves** | nothing to change |
| `bake-scope.sh`, `carry-forward-bundles.sh`, the gate's `--storage-target` fallback | directly, `az storage file` | each caller's `platform-ref` — **the same pin as the writer** |

🚨 **The Azure-direct readers and the writer travel together.** `node-repo-publish-bake.yml` fetches
`bake-scope.sh`, `carry-forward-bundles.sh` and `publish-bake-bundles.sh` from the platform at ONE
`platform-ref`, so a satellite gets all three or none. They therefore do **not** need to land ahead
of the writer — they land *with* it, in one commit, and no pin can carry half of it.

That leaves exactly one thing that must land first, and it is the one with the longest lead: the
**portal image**.

### Phase 1 — readers tolerate the pointer *(landed)*

`PublicationDirectoryOf` plus every read routed through it: the boot seeder, `PublishedBundleCatalogue`,
`ServedModuleBytes`, and the registry's four prebuilt routes. Behaviour on a share with no pointer
is unchanged, byte for byte.

Nothing writes a pointer yet, so this changes nothing observable — which is why it ships with a
suite that *builds the generation layout by hand* and asserts the readers serve it, including the
arm that catches the compose-under-the-source-directory mistake.

### Phase 2 — every producing repo's publish-bake pin moves past phase 1

The producers are core CD's own `plugins-bake` and each satellite's `publish-bake`. Phase 3 cannot
start while any of them still runs the flat writer, and the reason is specific:

🚨 **A new writer and an old writer on one prefix is the one genuinely broken intermediate state.**
The new one writes a generation and moves the pointer; the old one replaces the flat copy in place
and never touches `_current`. A pointer-following reader then keeps serving the generation and never
sees the old writer's newer publication at all — a *stale* serve, silent, and worse than the mix,
because nothing anywhere is red. **This is the half-migration to avoid**, and the guard against it is
ordering, not code.

### Phase 3 — the writer publishes a generation and swaps the pointer

`publish-bake-bundles.sh` uploads into `<source>/<publication token>/`, verifies (the #3496
postcondition still applies, now over a directory nobody else writes), seals, and moves `_current`
last. It **also** writes the flat copy for one release, so a portal image that predates phase 1 —
one that is deployed but has not rolled — keeps working. `bake-scope.sh` and
`carry-forward-bundles.sh` resolve the pointer in the same commit, and the carry-forward must read
the generation the *listing* came from, which is the shell analogue of the reader's `If-Match`.

### Phase 4 — drop the flat copy

Once no deployed portal predates phase 1. From here the mix is unrepresentable and the republish
window is gone; what remains is the sub-second pointer write described above, and an atomic rename
removes even that.

## Where this stands

- **Phase 1 is landed** — the readers resolve the pointer, and the fallback is the previous
  behaviour exactly.
- **Phases 2–4 are open**, tracked on
  [#3461](https://github.com/Systemorph/MeshWeaver/issues/3461). Until phase 3 ships, **the window
  is shrunk, not closed**: the publisher's postcondition still carries the whole load, and the
  interval between its last verification read and the seal is still live.
- The reds the postcondition produces are the correct number and must not be loosened away — see
  [Sealed Publication Reads](../SealedPublicationReads) → "What is NOT closed".

## Verification

- `test/Memex.Portal.Shared.Test/PublicationGenerationTest.cs` — builds the generation layout on
  disk and asserts what is served. The fixtures make the flat copy and the generation differ in
  **bytes under the same file names**, so "which publication was read" is a fact off the archive
  rather than an inference from a path. It covers the pointed-to read, the flat fallback, every
  escape shape, a dangling pointer with and without a flat copy behind it, a blank pointer, a
  retained older generation, and that moving the pointer moves the generation token (without which
  the `412` that stops an N+1 read spanning two publications never fires).
- **Negative controls, run against this tree.** With `PublicationDirectoryOf` reduced to the
  pre-#3461 reader (`return sourceDirectory`), 3 of 15 cases fail — the three that require
  resolution — and the other 12 hold, because they pin the fallback, which is unchanged. With the
  name validation removed but resolution kept, 4 more fail: the escape shapes. A resolver that
  cannot fail either check is not a resolver.

Related: [Sealed Publication Reads](../SealedPublicationReads) ·
[CI Content Bake](../CiContentBake) · [Plugin Build Contract](../PluginBuildContract) ·
[Bake Identity Mismatch](../BakeIdentityMismatch)
