---
Name: Module-Owned Siblings Ride
Category: Architecture
Description: The decision behind #3221 — a module-owned MeshWeaver.* sibling that another package DECLARES as its module still rides into a second bundle, and the invariant that closes the double-production hazard is byte equality, not exclusivity.
Icon: /static/NodeTypeIcons/code.svg
---

# Module-Owned Siblings Ride

A module-owned `MeshWeaver.*` sibling **rides** every bundle that references it
([Module Closure Accounting](../ModuleClosureAccounting)). Some of those siblings are *themselves*
another package's declared module. That makes one assembly name reach a mesh from several bundles at
once — two module-side producers of one name — which is the shape that
[#3175](../ModuleBuildArchitecture) exists to be afraid of.

This page records the decision (**it rides**), the evidence, and the invariant that replaces the
exclusivity the question proposed.

## The question

> Should a sibling that is **itself another package's declared module** ride into a second bundle,
> or should the closure rule exclude declared modules and let the landing resolve them from their
> owning package?

`BakeHost.ShippedByHostProblem` already refuses a module composed with `--module` that the *platform
host* also ships. It does not look at whether a *second module bundle* carries a copy of the same
assembly name. The proposed extension — refuse a bundle carrying an assembly another package
declares as its module — is the same shape one hop further out.

## The decision: it rides

**The closure rule is unchanged.** A module-owned sibling rides, whether or not another package
declares it. The refusing guard is **not** built, and the reason is not caution — it is that the
rule it would enforce is unsatisfiable.

### 1. It is the dominant shape, not an exception

Measured over `MeshWeaver.Plugins` `main` on 2026-09-03 — every package's `index.json`
`content.module`, joined against each module project's transitive in-repo `ProjectReference` closure
minus `src/platform-shipped.txt`:

| | |
|---|---|
| Declared modules in the repo | **37** |
| Module bundles riding at least one *other package's declared module* | **19** |
| Bundles riding `MeshWeaver.Markdown.Collaboration` (declared by **Essentials**) | 14 |
| Bundles riding `MeshWeaver.AI` (declared by **AI**) | 12 |
| Bundles riding `MeshWeaver.Maps` (declared by **Maps**) | 4 |

The issue named one instance (`Chat` riding `MeshWeaver.AI`) as a "pre-existing accepted pattern".
It is more than half the fleet's modules.

### 2. Excluding declared modules inverts the package graph

If a declared module may not ride, the landing must resolve it from its owning package — so the
riding package must **depend** on the owning one. It does not, and cannot:

```
AI/index.json         requires: [Store]                    module: MeshWeaver.AI
Essentials/index.json requires: [AI, Store, Export, …]     module: MeshWeaver.Markdown.Collaboration
```

`MeshWeaver.AI` references `MeshWeaver.Markdown.Collaboration`, which **Essentials** declares — and
Essentials `requires` AI. Making AI resolve the sibling from Essentials is a cycle. The same
inversion holds for `Mcp`, `Mail`, `Teams`, `Notifications`, `Observability` and every other package
Essentials requires: each rides an assembly its own dependent declares.

### 3. It would break a supported install

`AI` is installable on its own (`requires: [Store]`). With the sibling excluded, that bundle lands
without `MeshWeaver.Markdown.Collaboration.dll` and nothing supplies it — the platform image does
not (Plugins#1268 removed it from `/app`) and no required package brings it. The result is the
`ReflectionTypeLoadException` on first touch that
[Module Closure Accounting](../ModuleClosureAccounting) was written after: *"Could not load file or
assembly …"*, a red trunk in a repo that changed nothing, for eleven hours.

🚨 **That third argument is CONDITIONAL on the image, and the image moved.** Plugins#1515 seeds
`MeshWeaver.Markdown.Collaboration` (and `MeshWeaver.AI`, `MeshWeaver.Blazor.Chat`,
`MeshWeaver.Mcp`) into the portal image under `/app/modules/<Name>/`, so *today* the image DOES
supply it and the ride is both unnecessary and harmful. The rule is therefore not "declared modules
ride" but "a sibling rides **iff** the platform host does not ship it" — the same predicate, now
measured rather than assumed. Since MeshWeaver#3732 the packer answers it off the pinned image
itself: see [The Platform-Shipped Witness](../PlatformShippedWitness). The decision below stands
unchanged for every sibling the image genuinely does not carry, which is what the argument was
always about.

### 4. The host case and the module case are not the same defect

`ShippedByHostProblem` refuses host-vs-module because the two provenances use **incompatible id
schemes**. The host's copy resolves from the surface manifest as `ref:<hash>`; a composed module
resolves as `mvid:<guid>`. Those never compare equal *even for byte-identical assemblies*, so the
conflict is unfixable by agreement — one producer must go.

Module-vs-module is `mvid:` on **both** sides. Identical bytes compare **equal**. The conflict is
fixable by agreement, and agreement is cheaper than exclusivity.

🚨 **That last sentence was the wrong remedy, and #3934 replaced it.** "Agreement" here means every
producer of every copy emitting byte-identical output *forever*, and the section below measures why
that is unsatisfiable: Roslyn's deterministic MVID hashes the absolute source paths, so two lanes
compiling one file at two paths fork the MVID with identical properties. The CONSUMER half is the
fix — a module entry of a dependency record is now a **floor** (`min:<version>`), so a moved build
is not a mismatch at all. See [The Dependency Record Floor](../DependencyRecordFloor); everything
below about *why the copies diverge*, and the producer-side refusals, stands unchanged and is still
the right verdict for a torn publication.

## The invariant that replaces it

> **One assembly name, one framework identity, one build — across every copy in the sealed set,
> declared or riding.**

The hazard is real and worth an assertion. `MeshWeaver.*` assemblies bind by a strictly synchronised
`AssemblyVersion` (see [Module Closure Accounting](../ModuleClosureAccounting) → "the same-identity
trap"), so two copies under one simple name are **one assembly identity**: `Assembly.LoadFrom` returns
the already-loaded one and ignores the second path. Whichever copy loads first wins the process, and
the loser's bytes are never in memory.

What that costs when the copies differ is exactly the `#3175` incident:
`NodeTypeCompilationHelpers.ModuleMvidsOf` reports the MVID of the assembly that actually loaded, so
every NodeType whose dependency record named the other build is declined at adoption —
*"dependency record mismatch — 'MeshWeaver.Markdown.Collaboration' built against mvid:A, live is
mvid:B"* — and the view renders empty.

### 🚨 The copies do NOT agree, and the reason is structural (#3732)

This page used to say the copies agree by **accident** — that `bake-scope.sh` classifies any change
under `src/` as affecting ALL modules, and that `module-build-key.py` folds each entry's whole
in-repo `ProjectReference` closure into its content address, so a sibling's change re-keys every
bundle that carries it. Both statements are true. **The conclusion drawn from them was false**, and
the gap is one word: re-keying makes the bundles **REBUILD**, and says nothing about their BYTES.

**A publication is composed from several INDEPENDENT COMPILATIONS.** On `MeshWeaver.Plugins` there
are three per push, and a sibling project is compiled once in each of them:

| compilation | what it is | which bundles it produced |
|---|---|---|
| the **floor** lane's `build-workspace` | one Roslyn workspace inside the pinned image, for the first `module-pack` call (`Essentials`, `AI`, `Maps`, `Stripe`) | the DECLARED `MeshWeaver.Markdown.Collaboration`, and its ride inside `MeshWeaver.AI` |
| the **rest** lane's `build-workspace` | a second workspace, for the other `module-pack` call's `build: container` entries | its ride inside `Mcp`, `Teams`, `Mail.MicrosoftGraph`, `Observability`, `Notifications.Channels`, and the six `AI.*` providers |
| the legacy **`sdk`** entries | `dotnet build <project> -p:Version=<that module's version>` per module, on the runner — and `-p:Version` is a GLOBAL property, so every transitively referenced sibling is rebuilt under it | its ride inside `MeshWeaver.Blazor.Chat` |

Measured on `memex.meshweaver.cloud`, 2026-09-10: **one pod held
`MeshWeaver.Markdown.Collaboration` in 15 copies and THREE builds**, and the three groups match the
three compilations entry for entry — 12 copies riding the `rest` lane's container modules, 2 sharing
the `floor` lane's build (the declared module and its ride inside `MeshWeaver.AI`), 1 riding the one
`sdk` module that reaches it. Nothing was stale, nothing had drifted: 14 rides + 1 declaration = 15
copies, exactly as the closure rule intends, in as many builds as there were compilations.

Two consequences, both measured:

* **The bake host picks one by arrival order, and its pick becomes the contract.** The gate and bake
  lanes compose every module into `/ext/<Name>/` and hand them all to one process, which loads the
  first copy it sees and stamps `mvid:` of THAT build into every NodeType's dependency record. On
  memex the bundles named a build (`798f92a0…`) that was on no pod at all.
* **Two replicas holding two builds never converge.** Each declines the other's record
  (`HasStaleFrameworkBuild`), recompiles locally, stamps its own — `v2031 → v2050 → v2053`,
  alternating between two `compiledModulesHash` values with `currentSourceFingerprint` constant. Every
  activation lands mid-ping-pong, exhausts `MaxRecompileAttempts` and falls to the default config, so
  `SocialMedia/Post`'s views (`Preview`, `Write`, `PostCard`) were simply absent and
  `/Posts/SavThankYou` rendered nothing. A recycle does not clear it; publishing the missing bundle
  does not clear it either.

### The producer asserts it, where every copy is first in one hand

The `ext-modules` composition step of `node-repo-gate.yml` and `node-repo-publish-bake.yml` digests
every `MeshWeaver.*` assembly of every bundle it composes, groups them by simple name, and **refuses
a set carrying one name at more than one build** — naming each copy's digest, the BUNDLE it was
sealed in, and whether it arrived as the DECLARED module or a RIDING sibling, which is the half that
says which producer to change (the same wording `UpdatePolicy.heldReason` uses when the consumer
finds it days later). It prints its denominator on every run, green or red, because a check pointed
at the wrong directory refuses nothing while ticking exactly like a clean measurement. Identical
copies PASS — that is the decision this page took, and `test-module-set-consistency.py` executes
both directions plus two falsification arms against the lanes' own extracted shell.

🚨 **The reading is taken per bundle, BEFORE the merge — never off the composed directory.** The
landing loop copies each bundle's module folder into `/ext/<module.assemblyName>/`, so two bundles
declaring the same entry assembly (an artifact bundle and a registry bundle — a precedence the lane's
own notice calls an accident of glob order, `*.nupkg` before `*.zip`) land in ONE directory and the
second `cp -R` overwrites the first. Measured against a scan of `/ext`: two bundles carrying
`MeshWeaver.Alpha` at two builds reported **one** copy and passed. Reading each bundle's own unpacked
folder has both, and the harness carries that case.

That placement is not incidental: it is the first moment every copy is in one hand, **and** the
moment the damage is done, since what the bake loads is what every consumer must then match.

### The consumer asserts it again, and turns a violation into a HOLD

**`PublishedBundleCatalogue.ArtifactsForIdentity` reads the MVID of every `MeshWeaver.*` assembly
each sealed module bundle carries — entry and riding sibling alike — and any name carried at two
MVIDs becomes a `SealedModuleSet.Conflicts` line naming both producers, their sources, their bundles
and the ROLE of each copy.** `ReleaseAvailability.IsUpdatable` then answers
`PackageAvailabilityKind.SealedSetInconsistent` for every package whose records bind that name: a
HOLD, not a roll. That is the maintainer's requirement — *"we must have a clear confirmation that all
plugins deployed to an instance are available for the correct platform version. If not the case ⇒
nothing goes"* — applied to the copies as well as the declarations.

Two boundaries are deliberate:

* **Only the DECLARED entry defines `MvidByModule`.** An instance registers declared modules as
  `InstalledModuleAssembly`, and that registration is what `ModuleMvidsOf` reports back as "live". An
  assembly that only ever rides is registered nowhere, so letting it define the set would judge
  records against bytes the instance never reports — a false HOLD, which
  [#1754](../ReleaseGates)'s `ReleaseGateApplicabilityTest` already records the cost of.
* **Only `MeshWeaver.*` names are judged.** A third-party diamond rides by design and versions
  independently; it does not collapse to one identity. Judging it would hold every bundle in the
  fleet for a property that was never claimed.

## What this does not close

**The assertions REFUSE the divergence; they do not remove the second compilation.** A repo whose
publication is composed from more than one compilation now goes RED instead of shipping — which is
the correct verdict for those bytes — but the way to stay green is a decision nobody has taken yet,
and there are exactly two:

1. **One compilation per publication.** Every bundle of one publication is packed from ONE workspace
   build. Today `MeshWeaver.Plugins` splits `module-pack` into a `floor` call and a `rest` call
   (deliberately — the floor's four bundles are what the gates compose, Plugins#1438) and still has
   FOUR legacy `sdk` entries (it had five until `Chat` was converted, below), each rebuilding its
   siblings under its own `-p:Version`. Merging the two calls, or making the second reuse the
   first's workspace output for shared siblings, is a lane change; converting the last `sdk` entries
   is the standing direction anyway (maintainer, 2026-09-01: *"one global one and finish"*).
   🚨 **A COMPILED-VERSION PIN DOES NOT REMOVE A PRODUCER, and this page said it did.** The claim
   here was that carrying core's #3022 pin into MeshWeaver.Plugins — core's own
   `Directory.Build.props` pins the COMPILED version attributes to the commit precisely so
   `-p:Version=` cannot move an assembly's MVID (*"two publishes of one commit fork the identity and
   the bake goes inert"*) — would remove the `sdk` producer *"without touching a lane"*. Measured
   2026-09-10, it does not, and no version property can:

   * **What the pin DOES fix, and why it was kept** (Plugins#1604): Plugins'
     `src/Directory.Build.props` set `AssemblyVersion`/`FileVersion` from the platform but left
     `InformationalVersion` at the SDK default `$(Version)`, so on the `sdk` lane the HOST module's
     package version was compiled into every sibling it rebuilt. Pinning it stops that LEAK — two
     `sdk` entries sharing a sibling now agree with each other instead of forking it once per module
     version. That is a real defect, and a different one.
   * **What it cannot fix**: Roslyn's deterministic MVID also hashes the **absolute source paths**,
     and neither core nor MeshWeaver.Plugins sets `DeterministicSourcePaths` / `PathMap` /
     `ContinuousIntegrationBuild`. The `sdk` lane compiles at `$GITHUB_WORKSPACE`; a
     `build-workspace` compiles the same file at `/repo` inside the image. Measured: the same
     commit, the same properties, and the same emitted `InformationalVersion` attribute, built from
     two different absolute paths, produced `dace9bf7-c458-47ce-90f8-6bd9a7c7fa07` and
     `202e6e4f-20fa-40bf-a9d1-ccbb0be2d62c`. The version attributes were IDENTICAL in both.

   So **removing a compilation is always a LANE change** — there is no props-only remedy, and a
   reading of this page that promised one cost a session's work before the MVIDs were compared.
   `MeshWeaver.Blazor.Chat`, the one `sdk` entry that reaches `MeshWeaver.Markdown.Collaboration`,
   was therefore moved to `build: container` in MeshWeaver.Plugins' `.github/workflows/ci.yml`
   (MeshWeaver#3732). That takes this assembly from THREE builds to **TWO**; the remaining two are
   the `floor` and `rest` `build-workspace` calls, whose merge is the separate remedy above.
🚦 **The refusal reaches a satellite only when that satellite MOVES ITS PIN, so the ordering is
free.** Every node repo consumes these lanes at a full sha (`uses:
Systemorph/MeshWeaver/.github/workflows/node-repo-publish-bake.yml@<40-char sha>`), and moving that
pin is a deliberate, reviewed act by each repo's own rule. So this assertion is INERT for
MeshWeaver.Plugins and every other satellite until its pin bump — which is the moment to land the
remedy below and the pin move together, rather than discovering the refusal on a publish that had
nowhere to go. Measured 2026-09-10: on today's bytes the strip that #3751 added would keep the
`MeshWeaver.Markdown.Collaboration` and `MeshWeaver.AI` rides out of the portal-pinned lane (the
image seeds both), but `MeshWeaver.Maps` is seeded by nothing and is declared by the `floor` call
while `Northwind` rides it from the `sdk` lane — two compilations, so that is where the refusal
would first speak.

2. **Stop the ride.** Argued and rejected above, and the argument still holds: `AI` `requires`
   `[Store]` while `Essentials` — which DECLARES `MeshWeaver.Markdown.Collaboration` — `requires`
   `[AI, …]`, so making `AI` resolve the sibling from `Essentials` is a package cycle. It becomes
   available only if the assembly MOVES to a package `AI` already requires. That is a packaging
   decision in MeshWeaver.Plugins, not a line edit here.

🚨 **A host-measured witness cannot decide this.** [The Platform-Shipped Witness](../PlatformShippedWitness)
drops a riding copy the platform host already ships, and its third reading —
`<app>/modules/<Name>/<Name>.dll`, the seeded-module lane — happens to cover
`MeshWeaver.Markdown.Collaboration` and `MeshWeaver.AI` **on the portal image**, because
Plugins#1515 seeds them there. It does not cover them on `mw-plugin-test`, which is built from
core and contains neither. One publication, two hosts, two answers, one seal: the duplicate is a
property of the PUBLICATION and only a publication-level reading settles it. That is why the
assertion above measures the composed set rather than the image.

The **second producer in time** is untouched: an instance that installed a module from the registry's
content-versioned package endpoint holds whatever *that* lane published last, while the sealed
publication carries the bytes the platform release rebuilt. Both sets can be internally consistent
and still disagree with each other. See
[Module Build Architecture](../ModuleBuildArchitecture) → "What the gate still cannot see" for the
two ways to close it (the seal composes the registry's bytes, or the instance adopts module bytes
for its identity from the sealed publication).

See also: [The Dependency Record Floor](../DependencyRecordFloor) ·
[Module Closure Accounting](../ModuleClosureAccounting) ·
[Module Build Architecture](../ModuleBuildArchitecture) ·
[Candidate Release Protocol](../CandidateReleaseProtocol) · [Release Gates](../ReleaseGates)
