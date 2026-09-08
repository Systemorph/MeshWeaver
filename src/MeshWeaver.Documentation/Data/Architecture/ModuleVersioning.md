---
Name: Module Versioning
Category: Architecture
Description: How a package's version is decided — what you author, what the build derives, why an unchanged version means "delivered to nobody", and the one blind spot that made twelve packages undeliverable.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6m0 8v6M4.9 4.9l4.2 4.2m5.8 5.8l4.2 4.2M2 12h6m8 0h6M4.9 19.1l4.2-4.2m5.8-5.8l4.2-4.2"/></svg>
---

# Module Versioning

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** Delivery is no longer keyed on SemVer alone: a rebuilt same-version bundle for this platform's identity is adopted, and a fallback generation re-examines every new build. Implemented in [PR #3661](https://github.com/Systemorph/MeshWeaver/pull/3661) (2026-09-08); the sections below describe the mechanism as it runs now.

**A package's version is not decoration. It is the ONLY thing that decides whether anything you
built reaches a portal that already has an older copy.** Get it wrong and the pipeline stays green,
the bytes reach the shelf, and no installation ever asks for them.

This page is the authoring reference for that number. For the *node* revision counter — a different
number entirely — see [MeshNode Versioning](/Doc/Architecture/MeshNodeVersioning). For what a module IS and how a
deployment activates one, see [Modules](/Doc/Architecture/Modules).

🚨 **The PLATFORM's version is a third number, and it is not authored here.** `PlatformVersion` has
exactly two shapes — `X.Y.Z-ci.<n>` for every continuous build and clean `X.Y.Z` for the release, no
`rc`, no `preview`, no labelled line, ever — stated authoritatively in
[Release Process & Versioning §1](/Doc/Architecture/ReleaseProcess). Read it before writing a
package's `content.minMeshVersion` **floor**: a floor is a platform version, and SemVer §11.4 ranks
pre-release identifiers as text, which is how 42 packages came to declare floors no shipping platform
could ever satisfy (#3554). 🚨 A floor is an **authoring** claim checked at pack time
(`check-module-platform-floor.py` refuses one above the platform the bundle is built against) — at
runtime it is advisory and loadability is measured, never declared
([issue #3648](https://github.com/Systemorph/MeshWeaver/issues/3648)). Getting it wrong therefore
fails your build; it is not a knob for keeping a module off a deployment.

## The three numbers, and who owns each

| number | lives in | owned by |
|---|---|---|
| **MAJOR.MINOR** — the series | the package root's `index.json` → `content.version` | **you**, by hand |
| **PATCH** | `manifest.lock` → `version` | **the build** (`gen-manifests.py`), never you |
| **`moduleVersion`** — a content hash | `manifest.lock` | the build. Unordered; nothing can pin against it |

You author a series. The build derives the patch against the last published release and settles it
on `main` in `tag-modules`, so a branch never races the trunk for a number.

- **Bump the MINOR for a feature. Bump the MAJOR for a break.** That is the whole authoring rule.
- 🚨 **Never hand-edit the PATCH.** It is derived. A hand-set patch is a claim about a tree you did
  not measure.
- 🚨 **Never commit a node's `version` field.** That is `MeshNode.Version`, the owner's persistence
  clock — not authorable content. A committed counter collides with the durable row on a fresh
  mesh, `MonotonicWriteGuard` refuses the write, and the symptom surfaces four causal steps away as
  *"never reached compilationStatus Ok"*. Strip `version`, `lastModified` and `lastModifiedBy` from
  anything you copy out of a live mesh.
- 🚨 **A published version describes exactly one tree, forever.** `tag-modules.py` refuses to move
  an existing tag onto different content. Bump; never re-cut.

## Why an unchanged version means "delivered to nobody"

Module delivery is keyed on **SemVer and nothing else**. Two independent gates close on an unchanged
version, and neither looks at the bytes:

- `ModuleUpdateDecision` — `landedComparison == 0` → `SkipUpToDate`. The verdict is taken **before
  any download**, and the fetch carries no `If-None-Match`, so there is no byte-level fallback.
- `CatalogLayoutAreas` — the manual **Update** button returns `InstallResult(0, 0)` on
  `ModuleVersion` equality, **before** the module half is even composed. The human override does not
  override.

CI still rebuilds the assembly and POSTs it to `bundles/<Package>?version=<unchanged>`. The shelf is
correct. Nobody requests it. **A fresh install gets the new code; every existing portal keeps the old
one forever** — and the repo, CI and the registry all look green. This is
[MERGED is not delivered](/Doc/Architecture/DeployingPluginChanges) one layer down.

## 🚨 The blind spot: `src/` and mixed packages

A **mixed package** declares `content.module` and ships an assembly built from `src/`.

`gen-manifests.py` enumerates packages with `plugin_dirs()`, which skips the set the repo declares
in `scripts/gen-manifests.config.json` (see "One checker, every repo" below):

```json
{"skip": ["src", "test", "scripts", "tools", "e2e", "app", "clients", "meshweaver", "…"],
 "hashModuleSources": true}
```

That skip is deliberate and correct for *enumeration* — `src/` is not a package, and skipping it is
what keeps this gate independent of step order and of `validate-repos.py`. **Do not "fix" this by
deleting `"src"` from the skip list.** That changes what `moduleVersion` means for every package.
The narrower, correct switch is `hashModuleSources`.

The defect was narrower: for a mixed package, the module's own source under `src/` was **never hashed
into `moduleVersion`** — so a change confined to `src/` moved **no version, by construction**, and was
therefore built, shelved and delivered to nobody.

Measured on 2026-08-29: **12 of 29 mixed packages** were in exactly that state. The worst was the AI
engine itself — `AI/manifest.lock` on `main` was byte-identical to the tag `AI/v1.0.0`
(`version 1.0.0`, `moduleVersion be7ff235b95fb8e4`) across **205 changed `src/` files**. Since
`MeshWeaver.AI` left the portal image, the module bundle is its only delivery channel, and
`Agent/` + `Skill/` content ships as embedded resources inside that assembly — so an edit to a
built-in agent reached nobody, silently.

**The fix belongs in the derivation, not in a checklist.** For a package declaring
`content.module`, the hash must cover what actually ships in that bundle, so a `src/`-only change
moves the hash, moves the derived patch, and is delivered. Nothing to remember.

### What "what actually ships" means — the closure is narrower than the reference graph

Do not hash the whole dependency network. A bundle's contents are decided by `DepsClosure.Derive`,
which walks the module's own `deps.json` from its direct references and **stops at `MeshWeaver.*`
nodes** — those are never bundled, because a bundled copy would shadow the one in `/app`. Diamonds
ride deliberately: `/app` wins in the default load context while the platform carries a copy, and
the module's copy takes over when the platform sheds it, which is what lets platform slim-downs
land with no re-land coordination.

So for a mixed package the hash should cover:

| | in the hash? | why |
|---|---|---|
| the module project's own sources | **yes** | they are the assembly |
| the module's `.csproj` | **yes** | it pins package versions, and a NuGet bump changes the shipped bundle |
| non-`MeshWeaver.*` transitive deps | **yes**, by resolved version | they are bundled alongside the module |
| **module-owned** `MeshWeaver.*` project references | **yes** — and today they are NOT | they RIDE in the bundle (see below), so changing one changes this module's bytes |
| **image-shipped** `MeshWeaver.*` project references | **no** | never bundled — `/app` supplies them, so changing one does not change this module's bytes |

#### 🚨 `MeshWeaver.*` is not one row — the container lane split it in two

The version derivation implemented for the fix above hashes the module's **own project alone**,
on the reasoning that a `MeshWeaver.*` reference is never bundled. **That reasoning is only half
true, and the half it misses is live.**

`DepsClosure.Derive` does stop at `MeshWeaver.*`, and that is what the `sdk` pack path uses
(`--deps-closure`). The **container** path — now the default for nearly every entry — does not use
it. It reads the module's closure manifest and, for every `MeshWeaver.*` sibling in it, asks
`module-owned-platform.sh` one question: *is this project's source in this repo's `src/` and
absent from `src/platform-shipped.txt`?* If yes, the sibling **is copied into the bundle** and
passed as `--with <Name>.dll`, and the job log says so:

```
closure: MeshWeaver.Blazor.dll RIDES — module-owned (its source is in this repo's src/,
         so it is nowhere in the image's /app)
```

It must ride: nothing else would supply it at run time. But the version derivation does not know
that, so **a bundle can change its bytes without moving its version** — the exact defect the
`src/` fix above was written to remove, one hop out.

**Measured on `MeshWeaver.Plugins`, 2026-09-01** (35 matrix entries, 119 module-owned
`MeshWeaver.*` projects): `MeshWeaver.Blazor` is module-owned and rides in **7** published
bundles — `Analysis`, `AppleMaps`, `EntityViews`, `GoogleMaps`, `GraphViews`, `OpenStreetMap`,
`Radzen`. Not one of those seven `manifest.lock` files hashes a single `src/MeshWeaver.Blazor/`
path. An edit there changes what all seven bundles ship and moves none of their versions, so every
existing portal keeps the old copy — `SkipUpToDate`, before any bytes are read.

The `MeshWeaver.AI` example in the row above is *currently* safe only by accident of a transition:
`MeshWeaver.AI` is listed in that repo's `src/platform-shipped.txt` while `MeshWeaver.Blazor.Portal`
still references the engine. That line is marked to leave with MeshWeaver#2599 — **and the moment
it does, every AI-provider bundle joins the seven above.** Do not read "a change to
`MeshWeaver.AI` does not require bumping `MeshWeaver.AI.OpenAI`" as a standing rule; it is a
statement about one entry in one exclusion list on one day.

**The correct scope is the RIDING closure, not the whole reference graph.** Hash the module's own
project plus exactly the `MeshWeaver.*` siblings `module-owned-platform.sh` says ride in its
bundle. That is neither "own project only" (which under-covers, as measured) nor "the whole
closure" (which bumps every dependent on any change and destroys the signal in the number) — it is
the set whose bytes are actually inside the artifact being versioned.

The ProjectReference walker for this already exists — see `scripts/check-surface-manifest.py`
(`assembly names reachable from start through ProjectReference`), `scripts/project-closure.py`,
and the floor rule in `scripts/check-module-floors.py`. Reuse one of those rather than writing a
fourth; the module-owned/image-shipped split is `.github/scripts/module-owned-platform.sh`, the
same script the pack step and the bundle inspection call, so the three cannot disagree.

**Until that lands, `registry-version ≠ manifest-version` is NOT a sound publication baseline.**
It is proposed periodically as a way to narrow the module lane on `push` (Plugins#889 option 3);
version equality does not imply byte equality while any sibling rides, so narrowing on it would
silently under-publish exactly the bundles above. A sound baseline has to be a **commit** — the
analogue of the bake's `source-commit.txt` — diffed with `project-closure.py`, which walks
transitive in-repo `ProjectReference`s and therefore sees riders for free.

> **If you are reading this while that derivation is still landing:** bump `content.version`'s MINOR
> by hand for any `src/`-only change to a mixed package, and say in the PR that you did so and why.

## Full rebuild when the platform updates

A module is built against a platform pin. **When the platform releases, the pin moves and EVERY
module must be rebuilt and republished** — not only the ones whose source changed — or every portal
reads `FrameworkDeclined (built against <old>, live <new>)` and adopts nothing.

So the two lanes differ deliberately:

| trigger | bake lane | module lane |
|---|---|---|
| `pull_request` / `merge_group` | the **affected** closure | the **affected** closure |
| `push` to `main` | the **affected** closure, baselined on the PUBLICATION (`source-commit.txt`), never `github.event.before` | **everything** — this lane records no publication marker, and the registry version is not one (see the riding-closure note above) |
| `repository_dispatch` / `schedule` (release-follow) | **everything** | **everything** |
| `workflow_dispatch` | **everything** | **everything** |

The `push` row is the only one where the two lanes differ, and the difference is a **missing
marker, not a policy**: `bake-scope.sh` reads a `source-commit.txt` sealed beside the bundles, so it
knows the commit its own last publication covered. The module lane's scope still answers FULL on a
push — but since 2026-09-02 the **module build ledger** sits below the scope
([ModuleBuildArchitecture](/Doc/Architecture/ModuleBuildArchitecture) → "Content-addressed outputs"):
every selected module is keyed by its whole compiled+tested closure plus both image digests and the
platform ref, and a key the ledger already holds as Published is reused, not rebuilt. So a push
compiles *every module whose key has no usable Published record* — the Plugins#889 baseline, derived
from content rather than from a version or a commit, which is what makes it immune to the riding
blind spot above. Callers opt in with `ledger: required`.

## One checker, every repo

`gen-manifests.py` is the **platform's**, at `.github/scripts/gen-manifests.py`. `node-repo-validate.yml`
fetches it at the caller's pinned `platform-ref` and runs it against the caller's tree, exactly as the
compile-check lane runs `.github/scripts/compile-check.py` (AGENTS.md → *"never hand-roll a node repo's
CI"*; [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) → *"scripts are centralized —
the lane fetches the platform's copy at the pin; repos keep only allow-files"*).

It was vendored per repo until 2026-09-07, and by then the six copies were five different vintages —
MeshWeaver.Plugins 1157 lines, Crm 646, Education 646, Reinsurance 644, SocialMedia 643, Manufacturing
305. Each fix landed in whichever copy hit the bug and the other five kept it: `#434` (a release has two
witnesses), `#942`/`#1023` (`--resolve`), and `#1426` — the trunk baseline resolved against the remote's
**live tip**, which reds every intermediate commit of a merge-queue group on `main`, because the tree
under test is compared against locks committed *after* it. Manufacturing's copy never grew the trunk
witness at all, so it also had no `--resolve` and no self-test.

**What stays in the caller** is one allow-file, `scripts/gen-manifests.config.json`:

| key | |
|---|---|
| `skip` | **required** — the top-level directories that are NOT packages. Per-repo by construction (a scratch directory in one repo is a shipping package in another), and it must equal `validate-repos.py`'s `SKIP`, which each repo's `check-skip-sets.py` asserts. There is no default: a guessed skip list either demands a `manifest.lock` for `scripts/` or silently stops versioning a real module. |
| `hashModuleSources` | optional, default **false** — the #878 fix (hash a mixed package's `src/` project, and the siblings riding its bundle, into its `moduleVersion`). It needs the caller's `scripts/project-closure.py` to expose `graph_of` / `module_owned` / `riding_siblings`, and asking for it without one is an **error**, never a quiet fall-back to the smaller hash. Default false because turning it on **moves every mixed package's version** — a release event, not a script upgrade. |

Both settings are *declared*, never inferred from whether a file happens to exist: a capability that
degrades silently on a missing input is the skip-trapdoor shape AGENTS.md forbids, and here it would
change what a published version means without saying so.

Adoption is one commit per repo — add the config, drop the vendored copy, point `validate-repos.py`
and `check-skip-sets.py` at the platform copy, and pass `centralized-gen-manifests: true` to the lane.
Before merging one, prove it moves nothing:

```bash
MW_REPO_ROOT=$PWD python3 <platform>/.github/scripts/gen-manifests.py --check
MW_REPO_ROOT=$PWD python3 <platform>/.github/scripts/gen-manifests.py --check-versions
```

Both must be green **on the tree as committed** — the canonical has to reproduce every lock the
vendored copy wrote, or the swap republishes modules that did not change. Measured that way on
2026-09-07 across all six repos: identical verdicts everywhere, and Manufacturing gained the trunk
witness (`verified against their tags` → `verified against the published tags and the trunk`).

## Before you open the PR

```bash
python3 scripts/platform-script.py gen-manifests.py         # after ANY change to a package folder
                                           # OR to a src/ project a package bundles — both move the lock
python3 scripts/platform-script.py gen-manifests.py --check # what CI runs on your branch
```

`platform-script.py` resolves the platform's copy at the sha this repo's lanes are pinned to, so a
local verdict is CI's verdict. A repo that has not adopted yet still runs `python3 scripts/gen-manifests.py`.

`--check` is a **PR** gate and is entirely local: it asserts your lock describes *your* tree.
`--check-versions` is **main-only** — a branch is never asked to win a race against the trunk.

**The question to ask before merging is not "is it green" but "will anyone receive it":**

1. Did I change a package's node content? → the hash moves and the patch is derived, but **you still
   run `gen-manifests.py` and commit its output**. "Derived, not hand-edited" does not mean
   "generated for you": CI regenerates-and-commits only on `main` (`finalize-versions`), and on a
   branch it merely runs `--check`, which reds `Validate node repos` naming every stale module.
2. Did I change only `src/` of a mixed package? → **the same applies** — editing `src/` alone moves
   the lock of every package that bundles that project, often a dozen at once. Run the generator,
   then confirm `manifest.lock` actually moved: if it did not, the change reaches nobody.
3. Is this a feature or a break? → bump the MINOR or the MAJOR in `index.json` by hand.
4. Am I tempted to edit the patch, or to re-cut an existing tag? → no.

## Related

[Modules](/Doc/Architecture/Modules) · [MeshNode Versioning](/Doc/Architecture/MeshNodeVersioning) ·
[Plugin Packaging](/Doc/Architecture/PluginPackaging) · [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) ·
[Plugin Registry](/Doc/Architecture/PluginRegistry) · [Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild)
