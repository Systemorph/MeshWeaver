---
Name: Module Versioning
Category: Architecture
Description: How a package's version is decided — what you author, what the build derives, why an unchanged version means "delivered to nobody", and the one blind spot that made twelve packages undeliverable.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6m0 8v6M4.9 4.9l4.2 4.2m5.8 5.8l4.2 4.2M2 12h6m8 0h6M4.9 19.1l4.2-4.2m5.8-5.8l4.2-4.2"/></svg>
---

# Module Versioning

**A package's version is not decoration. It is the ONLY thing that decides whether anything you
built reaches a portal that already has an older copy.** Get it wrong and the pipeline stays green,
the bytes reach the shelf, and no installation ever asks for them.

This page is the authoring reference for that number. For the *node* revision counter — a different
number entirely — see [MeshNode Versioning](/Doc/Architecture/MeshNodeVersioning). For what a module IS and how a
deployment activates one, see [Modules](/Doc/Architecture/Modules).

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

`gen-manifests.py` enumerates packages with `plugin_dirs()`, which skips a fixed set:

```python
SKIP = {"src", "test", "scripts", "tools", "e2e", "app", "clients", "meshweaver", ...}
```

That skip is deliberate and correct for *enumeration* — `src/` is not a package, and skipping it is
what keeps this gate independent of step order and of `validate-repos.py`. **Do not "fix" this by
deleting `"src"` from `SKIP`.** That changes what `moduleVersion` means for every package.

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
| `MeshWeaver.*` project references | **no** | never bundled — they ship in their own bundles or come from `/app`, so changing one does not change this module's bytes |

That last row is the one worth stating out loud: **a change to `MeshWeaver.AI` does not require
bumping `MeshWeaver.AI.OpenAI`.** Modules bind platform types by simple assembly name, so the
dependent's bytes are unchanged; `MeshWeaver.AI` ships its own bundle at its own version. Bumping
every dependent would be busywork that also destroys the signal in the version number.

The ProjectReference walker for this already exists — see `scripts/check-surface-manifest.py`
(`assembly names reachable from start through ProjectReference`) and the floor rule in
`scripts/check-module-floors.py`. Reuse one of those rather than writing a fourth.

> **If you are reading this while that derivation is still landing:** bump `content.version`'s MINOR
> by hand for any `src/`-only change to a mixed package, and say in the PR that you did so and why.

## Full rebuild when the platform updates

A module is built against a platform pin. **When the platform releases, the pin moves and EVERY
module must be rebuilt and republished** — not only the ones whose source changed — or every portal
reads `FrameworkDeclined (built against <old>, live <new>)` and adopts nothing.

So the two lanes differ deliberately:

| trigger | scope |
|---|---|
| `pull_request` | the **affected** closure |
| `push` to `main` | the **affected** closure, baselined on the PUBLICATION (`source-commit.txt`), never `github.event.before` |
| `repository_dispatch` / `schedule` (release-follow) | **everything** |

## Before you open the PR

```bash
python3 scripts/gen-manifests.py           # after ANY change to a package folder
python3 scripts/gen-manifests.py --check   # what CI runs on your branch
```

`--check` is a **PR** gate and is entirely local: it asserts your lock describes *your* tree.
`--check-versions` is **main-only** — a branch is never asked to win a race against the trunk.

**The question to ask before merging is not "is it green" but "will anyone receive it":**

1. Did I change a package's node content? → the hash moves, the patch is derived. Nothing to do.
2. Did I change only `src/` of a mixed package? → confirm `manifest.lock` actually moved. If it did
   not, the change reaches nobody.
3. Is this a feature or a break? → bump the MINOR or the MAJOR in `index.json` by hand.
4. Am I tempted to edit the patch, or to re-cut an existing tag? → no.

## Related

[Modules](/Doc/Architecture/Modules) · [MeshNode Versioning](/Doc/Architecture/MeshNodeVersioning) ·
[Plugin Packaging](/Doc/Architecture/PluginPackaging) · [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) ·
[Plugin Registry](/Doc/Architecture/PluginRegistry) · [Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild)
