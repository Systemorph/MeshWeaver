---
Name: The Platform-Shipped Witness
Category: Architecture
Description: What a module bundle may carry beside its entry is decided by MEASURING what the platform host actually ships — its app closure, its surface manifest and its seeded modules/ lane — never by a declared name list. The three witnesses, why a list drifts in both directions, and where the measurement still cannot reach.
Icon: /static/NodeTypeIcons/code.svg
---

# The Platform-Shipped Witness

A module bundle carries its entry assembly plus a private closure. Which `MeshWeaver.*` siblings may
ride that closure has exactly one correct answer, and it is a property of the **platform host the
bundle is packed against**, not of the repository the module lives in:

> A `MeshWeaver.*` sibling rides a bundle **if and only if** the platform host does not already
> ship it.

Both directions of that rule are load-bearing, and each has cost a fleet-wide incident:

* **Carrying one the host has.** `MeshWeaver.*` assemblies bind by a strictly synchronised
  `AssemblyVersion`, so two copies under one simple name are **one assembly identity**: the loader
  keeps whichever it saw first and the loser's bytes are never in memory. Every NodeType whose
  dependency record named the other build is declined at adoption — *"dependency record mismatch —
  built against mvid:A, live is mvid:B"* — and the health check reports the loser as
  `pending_module_activation` **Degraded**, *"landed but not yet loaded"*.
* **Omitting one the host does not have.** The bundle lands without an assembly nothing else
  supplies, and the first code path that touches it throws `ReflectionTypeLoadException` — hours
  after the install, in a repository that changed nothing.

## What used to decide it, and why it drifted

Until [#3732](../ModuleBuildArchitecture) the split came from a hand-maintained file in each node
repository, `src/platform-shipped.txt`: every `MeshWeaver.*` project directory in `src/` **except**
the names that file lists. `.github/scripts/module-owned-platform.sh` computed the complement, and
the same set reached three consumers — the container build's closure copy, the packer's
`--own-platform`, and the bundle inspection's assertion.

A declaration is not a measurement, and this one went stale in **both** directions inside one week:

| when | what moved | what the list did | what it cost |
|---|---|---|---|
| Plugins#1023 | `MeshWeaver.AI` left the image | the line was correctly removed | — |
| MeshWeaver#3335 | five assemblies were in `/app` and unlisted | corrected **by hand** after a manual re-measurement | every bundle referencing one carried a duplicate |
| MeshWeaver#3335 | `MeshWeaver.Maps` had left the image | the line lingered | a name that reached a mesh **from nowhere at all** |
| Plugins#1515 | four modules returned under `Modules:Assemblies` | the list did not move | see the measurement below |

Measured on `MeshWeaver.Plugins` `main`, 2026-09-08 — every packed module's transitive in-repo
`ProjectReference` closure, minus `src/platform-shipped.txt`, against the seeded-module rows in both
portal hosts' csproj:

| | |
|---|---|
| module bundles the repository packs | **37** |
| bundles riding at least one `MeshWeaver.*` sibling | **18** (34 riding slots) |
| bundles riding a sibling **the image also ships** | **14** (27 slots) |
| the names | `MeshWeaver.Markdown.Collaboration` (14 bundles), `MeshWeaver.AI` (13) |

Every one of those 27 slots is a second build of an assembly a portal already has, and none of them
was visible to the witness that was supposed to prevent exactly that.

## The three witnesses

The reason the list could not simply be corrected once more is that **an image ships an assembly
three ways**, and the previous reading knew about one of them.

| # | Where | What puts it there |
|---|---|---|
| 1 | `<app>/<Name>.dll` | the application closure — a `ProjectReference` from a portal host |
| 2 | `<app>/meshweaver-surface.manifest` | the host's own record of its `MeshWeaver.*` **compile references** (`MeshWeaverSurfaceManifest.targets`) |
| 3 | `<app>/modules/<Name>/<Name>.dll` | the **seeded-module** lane — `@(MeshModuleClosure)` in `memex/MeshModulesPublish.targets`, which `MeshBuilder.ResolveModulePath` probes *before* the app directory |

🚨 **A `MeshModuleClosure` row touches neither (1) nor (2)** — the portal hosts' own csproj comments
say so in as many words. So a witness that read `/app` alone answered *"not shipped"* for every
seeded module, which is precisely the 27 slots above.

`PlatformShippedAssemblies.Read(appDirectory)` (in `MeshWeaver.Compiler`) reads all three and
returns each name with the **provenance and the evidence path** that answered, so a strip decision
in a pack log names a file rather than an opinion.

### It refuses rather than answering nothing

A directory with no surface manifest and no `MeshWeaver.*` assembly at its root is **not** a platform
application directory, and the reader returns a problem for it. Answering *"this host ships nothing"*
would turn every caller's strip into a silent no-op that logs exactly like a clean measurement — the
gate-that-cannot-fail shape [CI](../ReadingCiSignals) forbids. A portal `/app` carries 200+
`MeshWeaver.*` assemblies and the tester image 88; zero is a wrong directory, never an answer.

## Where the measurement is applied

Two places, deliberately, and they answer different questions.

**The classification** — `module-owned-platform.sh <src> [<platform-app>]` — decides what the deps
walk treats as module-owned, which matters beyond the `MeshWeaver.*` names themselves: walking into
a platform-shipped project also drags that project's private package dependencies into the bundle.
With an app directory it measures; without one it falls back to the declared list and **says so on
stderr**, because the fallback lane is structurally different (see the blind spot below), not because
the input happened to be missing. When both are available the image decides and the list decides
nothing — the disagreement is printed as a warning naming the exact lines to add or drop, so the
file converges and can eventually be deleted.

**The enforcement** — `module-pack --platform-app <dir>` — is a second, independent reading over the
bundle's actual file list. Everything upstream composes the closure from *declarations* (`--with`
from the lane's classification, `--deps-closure` from a graph walk seeded by `--own-platform`), and
those are shell-string sets matched with `grep -qF`; this one is a typed measurement taken at the
moment the bytes are written. Every `MeshWeaver.*` file in the closure is measured against the host
and **dropped** when the host already has it, with one printed line each naming the evidence.

🚨 **On a lane where the classification is also measured, the drop count should be ZERO — and that
makes it a control, not decoration.** Both readings answer the same question from the same image, so
a non-zero `platform-shipped: … dropped` line in a pack log means the two disagree, and the
disagreement is worth reading. It also means a caller that drives `module-pack` directly — a new
lane, a hand invocation, a satellite that has not adopted the shared workflow — gets the protection
without having to know about the classification at all. What it deliberately does NOT do is refuse:
shipping a correct bundle with a loud line beats reddening a delivery lane over a disagreement whose
resolution is already in hand.

The lane passes the **raw** image copy (`$RUNNER_TEMP/platform-refs`), never the
supersede-pruned reference set: `superseded-image-assemblies` drops an assembly from the *compile*
set precisely **because** the image still carries it, so measuring the pruned copy would answer
"not shipped" for a name that is right there in `/app`.

### What the witness does NOT judge

**The entry assembly.** A module the image seeds under `modules/<Name>/` is *meant* to be superseded
by a landed bundle of itself — a usable persisted entry overrides the same-named baseline in place —
so dropping the entry would delete the module from its own bundle. The host-versus-entry case is a
different defect with a different remedy, and it already has a gate:
`BakeHost.ShippedByHostProblem` refuses a module composed with `--module` that the platform host
also carries, at the bake, where both provenances are in one hand.

**Third-party assemblies.** A diamond rides by design and versions independently; it does not
collapse to one identity. Judging it would hold every bundle in the fleet for a property nobody
claimed. See [Module Closure Accounting](../ModuleClosureAccounting) for why "the image has it" is
never on its own a reason to leave a *package* assembly out.

## Which repositories this changes

`src/platform-shipped.txt` exists in exactly **one** repository of the fleet — `MeshWeaver.Plugins`,
where it names 17 assemblies. In the other five node repos it is absent, which the script reads (and
still reads) as *"the platform ships nothing"*: every `MeshWeaver.*` project in their `src/` is
classified module-owned and rides. That is right today only because those repos own few such
projects (`MeshWeaver.SocialMedia` has 2; the rest have none) — it was never a measurement, and it
becomes one for all six with no per-repo file to write.

## The blind spot, named

**A lane that pins no platform image has nothing to measure.** Core's own CD packs the four bundles
its bake composes with `platform-ref` (a source build), not `platform-image-digest`, so there is no
`/app` in that job at all and the classification falls back to the declared list. That fallback is
stated on stderr on every run rather than passing silently, and the enforcement step is simply not
armed there.

That is a real gap and not a trapdoor: it is one structurally different lane, named in the log, not
an `if:` that asks whether an input happens to be present. Closing it means giving that job the
promoted portal image the same run already resolves.

## Related

* [Module Build Architecture](../ModuleBuildArchitecture) — the one build shape every repo follows
* [Module Closure Accounting](../ModuleClosureAccounting) — what a bundle must carry, and the
  same-identity trap
* [Module-Owned Siblings Ride](../ModuleOwnedSiblingsRide) — why a sibling that is *another
  package's* declared module still rides, and the one-name-one-build invariant that replaced
  exclusivity
* [Module Versioning](../ModuleVersioning) — what you author and what the build derives
