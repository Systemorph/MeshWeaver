---
Name: The Platform Image's Closure
Category: Architecture
Description: The portal image is the reference set every satellite's modules compile against — and until 2026-09-05 nothing asserted what it contains. Two invariants, the two breaches that arrived on one day, and the gate that refuses to promote an image that violates either.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><polyline points="3.27 6.96 12 12.01 20.73 6.96"/><line x1="12" y1="22.08" x2="12" y2="12"/></svg>
---

# The Platform Image's Closure

**The platform image is the compiler and the reference set** — that is the first line of
[Module Build Architecture](../ModuleBuildArchitecture), and it is what makes the fleet's build
shape work: a module does not compile against a source tree or a feed, it compiles against the
assemblies the portal will actually load.

It also means the image's application directory is a **contract**, published to every satellite in
the fleet. Until 2026-09-05 nothing stated that contract and nothing checked it. **Every consumer
discovered what the image contains by failing to compile against it** — hours later, in a
repository whose own diff was innocent, on pull requests that had changed nothing relevant.

Two breaches arrived on the same day, from opposite directions, and neither was visible in the
repository that caused it.

## The two invariants

| | Invariant | What a breach costs |
|---|---|---|
| **1** | **One producer.** No assembly name composed as a MODULE may also be in the host's `/app` or its `meshweaver-surface.manifest`. | Every NodeType binding the name is DECLINED at adoption on every portal — `dependency record mismatch — built against mvid:…, live is ref:…` |
| **2** | **The set resolves.** No assembly in `/app` may bind a HIGHER version of another assembly in `/app` than the copy shipped beside it. | Every module bundle in every satellite fails `MSB3277` under the module lane's `-warnaserror`, with no change in their repositories |

Both are properties of the *pair* of artefacts, never of either one alone. That is exactly why
review does not catch them: each half is plausible, each half was written by someone with a good
reason, and the two halves live in different files — sometimes in different repositories.

## Breach 1 — a module's name in `/app` (#3327)

`MeshWeaver.Markdown.Collaboration` is the Essentials package's registry-served module. It was also
in every portal image, because `MeshWeaver.Blazor.Views` — which *is* in the image — held a
`ProjectReference` to it for the collaborative markdown view. Core's CD composes that same assembly
with `--module`, so a bake saw two builds of one simple name and refused the whole publication:

```
compile: FATAL — module(s) MeshWeaver.Markdown.Collaboration are composed with --module AND
shipped by the platform host at '/app' — two builds of one assembly name in one bake.
```

The rule and the refusal already existed — `BakeHost.ShippedByHostProblem` (#3175). **The gap was
WHEN it runs.** The bake happens after `promote`, so the image already carries a version tag every
satellite can pin, and the red lands in *their* repository, on *their* pull requests.

### The measurement that corrected the diagnosis

#3327 recorded that the pinned platform set `ci.7755` "was built AFTER Plugins#1268 (12:48:45Z), so
this is **not** stale-image lag". Re-measured against ACR, that is false in both directions, and the
correction matters because it changes what needed fixing:

| | |
|---|---|
| `memex-portal-ai:3.0.0-rc9.ci.7755` created | **2026-09-04T11:43:19Z** — *before* 12:48:45Z |
| its Plugins commit (from the `<core>-p<plugins>` tag) | `a9aaf84` |
| `ci.7757` | `f9d2a33-ped7b270` — Plugins `ed7b270`, PR #1327, 11:30:38Z |
| `ci.7758` | `b0d4a82-p2f7a95d` — Plugins `2f7a95d`, **the merge commit of #1268** |

Bisecting the extracted `/app` of every promoted image in the window:

```
ci.7756  MeshWeaver.Markdown.Collaboration.dll at /app root: yes
ci.7757  yes
ci.7758  no          ← Plugins#1268
ci.7794  no
```

So the repo-side fix landed, held, and the first image carrying it is `ci.7758`. The satellite red
was ordinary **stale-image lag on a pin that predated the fix** — the mechanism the issue explicitly
ruled out. What was genuinely missing was not another repo-side fix but the producer-side assertion,
which is invariant 1 above.

## Breach 2 — the set does not resolve (#3328)

Four module bundles failed against `3.0.0-rc9.ci.7779`:

```
error MSB3277: Found conflicts between different versions of "SQLitePCLRaw.core"
  … between "SQLitePCLRaw.core, Version=2.1.11.2622" and "…, Version=3.0.2.2801"
  "2.1.11.2622" was chosen because it was primary
  References which depend on … 2.1.11.2622: platform-refs-effective/SQLitePCLRaw.core.dll
  References which depend on or have been unified to … 3.0.2.2801:
      platform-refs-effective/Microsoft.CodeAnalysis.Workspaces.dll
```

`platform-refs-effective` is `docker cp <portal image>:/app/.`. So both sides of the conflict are
the image's own bytes.

**Where each version comes from.** Measured over the assembly metadata of the extracted image:

- `Microsoft.CodeAnalysis.Workspaces.Common` **5.9.0** declares exactly three dependencies —
  `Humanizer.Core`, `Microsoft.CodeAnalysis.Common`, `System.Composition`. **No SQLitePCLRaw.** Yet
  `Microsoft.CodeAnalysis.Workspaces.dll` carries `AssemblyRef`s to `SQLitePCLRaw.core 3.0.2.2801`
  and `SQLitePCLRaw.batteries_v2 2.3.5.0`: Roslyn's optional SQLite persistent storage is compiled
  in with the package dependency *excluded*, because the feature is only wired up in Visual Studio.
  **NuGet cannot see this consumer at all.**
- `MeshWeaver.Hosting.Sqlite` → `Microsoft.Data.Sqlite 10.0.10` → `SQLitePCLRaw.core 2.1.11`. That
  is the copy that lands.

**Neither half is a defect.** Roslyn has always shipped that dangling reference, and for as long as
no `SQLitePCLRaw.core.dll` was in `/app` it simply went unresolved and cost nothing — `ci.7755`'s
reference set is provably clean. The pair is the defect, and the pair was assembled by
MeshWeaver.Plugins#1284, *"First-run setup: SQLite becomes selectable"*, which added
`MeshWeaver.Hosting.Sqlite` to `Memex.Portal.Gui` so a fresh install can open its own store.

Diffing the `/app` roots of the two promoted images:

```
ci.7755 → ci.7794, LEFT /app root:      MeshWeaver.Markdown.Collaboration.dll   ← breach 1's fix
ci.7755 → ci.7794, ENTERED /app root:   MeshWeaver.Hosting.Sqlite.dll
                                        Microsoft.Data.Sqlite.dll
                                        SQLitePCLRaw.batteries_v2.dll
                                        SQLitePCLRaw.core.dll
                                        SQLitePCLRaw.provider.e_sqlite3.dll
```

Both changes rode the same image, `ci.7758`. One breach closing and another opening, in one build,
neither visible to anything.

### Why the issue's proposed fix would have changed nothing

#3328 proposed pinning the managed trio at **2.1.11**. That is what already resolves — the pin would
not have moved a byte. And the other direction is closed too: `SQLitePCLRaw` **3.0** restructured the
package family (`bundle_e_sqlite3` 3.0.2 depends on `SQLitePCLRaw.config.e_sqlite3` +
`SourceGear.sqlite3 3.50.4.2`; there is no `lib.e_sqlite3`), so moving the whole family forward would
silently replace the patched native engine pinned for GHSA-2m69-gcr7-jv3q with an older one.

**What actually works** — pin only the two managed assemblies whose version the image's *other*
consumer already binds, and leave the bundle and the native engine alone:

```xml
<PackageReference Include="SQLitePCLRaw.core"               VersionOverride="3.0.2" />
<PackageReference Include="SQLitePCLRaw.provider.e_sqlite3" VersionOverride="3.0.2" />
```

Verified three ways before landing (MeshWeaver.Plugins#1351):

- **The resolver.** A probe project referencing every assembly of the *real* extracted `/app`, built
  as the module lane builds (`-c Release -warnaserror`): `ci.7794` reproduces the identical MSB3277;
  the same directory with the two assemblies swapped to 3.0.2 gives `Build succeeded. 0 Warning(s)
  0 Error(s)`.
- **Binary compatibility, by metadata.** `Microsoft.Data.Sqlite.dll` 10.0.10 resolves **13/13 type
  refs and 77/77 member refs** against `SQLitePCLRaw.core` 3.0.2, none unresolved; the 2.1.11
  `batteries_v2` resolves against both 3.0.2 assemblies.
- **At runtime**, which metadata cannot prove: the composed stack opens a connection, executes a
  query, and answers `sqlite_version() = 3.53.3` — the patched engine, still the one loaded.

## The gate

[`.github/scripts/check-platform-reference-set.sh`](https://github.com/Systemorph/MeshWeaver/blob/main/.github/scripts/check-platform-reference-set.sh),
run as a step of `main-cd.yml`'s `portal-image` job over the publish output that becomes `/app`.
It runs **before `promote`**, which is the whole point: an image that violates either invariant
never receives a tag a consumer can select, so the red lands on the run that produced it instead of
on six satellites that did not.

**Invariant 1** compares the host's `/app` and surface manifest against the composed-module set —
**read out of `main-cd.yml` itself** (`jobs.plugins-modules.with.modules`), never restated. The
compose set and the set the image is forbidden to carry must be one list, or they drift, and a
drifted second list is how a gate stays green while asserting the wrong names.

**Invariant 2 does not model MSBuild's binding rules — it runs them.** The gate writes a throwaway
project whose `<Reference>` items *are* that directory and builds it with the module lane's own
flags. A hand-written version comparison would be a second opinion about what
`ResolveAssemblyReferences` does, and this entire defect class is two artefacts that were each
individually plausible.

The probe's reference set is faithful to the real one by construction. The portal repository's
`src/Directory.PlatformRefs.targets` — what `-p:MeshWeaverRefs=<dir>` actually drives — reads:

```xml
<Reference Include="$(MeshWeaverRefs)/*.dll"
           Exclude="$(MeshWeaverRefs)/$(AssemblyName).dll;@(ProjectReference->'$(MeshWeaverRefs)/%(Filename).dll')">
```

A **root-level glob**, which is why `app/modules/` seeds are outside the reference set and why the
probe globs the root only. The real build additionally *excludes* the module's own name and its
project references — module-owned names, never a third-party assembly — so the probe's set is a
superset of every module's. It can therefore miss no conflict a module would hit.

### Anti-vacuity

A gate that cannot fail is not a gate, so every input is asserted and a missing one is RED, naming
what to provision — never a skip, because GitHub paints a skipped step the same colour as a passed
one:

- the application directory must exist and hold **≥ 50** assemblies (the floor
  `node-repo-module-pack.yml` already uses; the portal ships ~214, the tester 88);
- `meshweaver-surface.manifest` must be beside them, naming **≥ 20** assemblies — its absence is
  either this gate pointed at the wrong directory *or* the manifest silently no longer being
  published, which on its own breaks NodeType bake adoption (#1699). Both are red;
- the composed-module set read from the workflow must be **non-empty**, or invariant 1 would pass
  having compared nothing;
- `ResolveAssemblyReferences` must report **≥ 50 resolved references**, or the probe compiled
  against nothing and its silence means nothing.

### Mutation proof

The gate was run against the real extracted `/app` of both promoted images and against the fixed
set, before the fix was written:

| reference set | invariant 1 | invariant 2 | exit |
|---|---|---|---|
| `ci.7755` `/app` (210 assemblies) | **RED** — `MeshWeaver.Markdown.Collaboration` in the closure *and* the manifest | OK (377 refs) | `1` |
| `ci.7779` `/app` — the version #3328 names | OK | **RED** — `MSB3277` | `1` |
| `ci.7794` `/app` (214 assemblies) | OK | **RED** — `MSB3277`, `SQLitePCLRaw.core` 2.1.11.2622 ↔ 3.0.2.2801 | `1` |
| `ci.7794` `/app` + the fix | OK | OK (381 refs) | `0` |
| a real local portal publish, fix applied | OK | OK (214 assemblies, 381 refs) | `0` |
| the same publish, fix reverted **in place** | OK | **RED** — `MSB3277` | `1` |

Each half fails on the image that actually broke and passes on the one that did not, so neither is
a blanket red and neither is decorative.

## What this gate still cannot see

- **Only the app ROOT.** `app/modules/<Module>/` seeds (the `MeshModuleClosure` lane) are not part
  of the reference set and are deliberately not compared — a modules/ seed is not a second producer,
  which is what lets a module stay in the image without breaching invariant 1 (Plugins#1268).
- **The declaration, not the bytes, for module ownership.** Running the *broader* predicate — every
  `MeshWeaver.*` project in the portal repository's `src/` that `platform-shipped.txt` does not name
  — flags five further names that `/app` ships today: `MeshWeaver.Blazor`,
  `MeshWeaver.Blazor.Portal`, `MeshWeaver.Hosting.Blazor`,
  `MeshWeaver.ContentCollections.Indexing` and `MeshWeaver.ContentCollections.Indexing.Graph`. The
  last two are named in `platform-shipped.txt`'s own comment as *measured ABSENT from `/app`*, with
  "do NOT re-add"; they are present in both `ci.7755` and `ci.7794`. That is the same
  declaration-versus-bytes gap this page is about, one layer over, and it interacts with the
  in-flight optional-Blazor work — so it is filed as #3335 rather than folded in here.
- **Runtime binding.** `/app` is a runtime directory and the runtime binds by simple name, ignoring
  version — which is why the portal runs perfectly with a reference set that will not resolve. This
  gate asserts the COMPILE contract, which is the one the fleet consumes.

## Related

- [Module Build Architecture](../ModuleBuildArchitecture) — the platform image is the compiler and
  the reference set
- [Module Versioning](../ModuleVersioning) — what you author and what the build derives
- [Plugin Packaging](../PluginPackaging) — bundles and the framework identity
- [Node Type Compilation](../NodeTypeCompilation) — what compiles at runtime and never in CI
- [Reading CI Signals](../ReadingCiSignals) — what a green wall is and is not evidence of
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the other shape of "one repo's merge reds
  another's trunk"
