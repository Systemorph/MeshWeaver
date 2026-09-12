---
Name: Module-Owned Siblings Ride
Category: Architecture
Description: The settled answer to #3221 — a module-owned MeshWeaver.* sibling rides every bundle that references it IFF the platform host does not already ship it; being another package's declared module is no reason to exclude it, and what closes the double-production hazard is one build per name per publication, asserted at the producer and again at the consumer.
Icon: /static/NodeTypeIcons/code.svg
---

# Module-Owned Siblings Ride

A module-owned `MeshWeaver.*` sibling **rides** every bundle that references it
([Module Closure Accounting](../ModuleClosureAccounting)). Some of those siblings are *themselves*
another package's declared module. That makes one assembly name reach a mesh from several bundles at
once — two module-side producers of one name — which is the shape that
[#3175](../ModuleBuildArchitecture) exists to be afraid of.

This page records the decision (**it rides, iff the host does not ship it**), the evidence, and the
invariant that replaces the exclusivity the question proposed. The answer, with today's
measurements, is the first section; the argument that reached it follows.

## The question

> Should a sibling that is **itself another package's declared module** ride into a second bundle,
> or should the closure rule exclude declared modules and let the landing resolve them from their
> owning package?

`BakeHost.ShippedByHostProblem` already refuses a module composed with `--module` that the *platform
host* also ships. It does not look at whether a *second module bundle* carries a copy of the same
assembly name. The proposed extension — refuse a bundle carrying an assembly another package
declares as its module — is the same shape one hop further out.

## Settled: the rule in its final form

**Measured 2026-09-11, and this is the answer to the title question.** The rule is not "declared
modules ride" and not "declared modules are excluded". It is:

> **A module-owned `MeshWeaver.*` sibling rides every bundle that references it, IFF the platform
> host does not already ship it — and being another package's declared module is not a reason to
> exclude it.** What makes the ride safe is not exclusivity but **one build per name per
> publication**, asserted by the producer at compose time and by the consumer over the whole sealed
> set.

Three measurements, each with its denominator, and the third is the one the question was opened
about.

### 1. How often the shape occurs — the reference graph

`MeshWeaver.Plugins` `main` at `d6fa0338`, every `index.json` in the tree joined against each
declared module project's transitive in-repo `ProjectReference` closure:

| | |
|---|---|
| `index.json` files scanned | 79 |
| of those, declaring a `content.module` | **37** (37 distinct assembly names; none declared twice) |
| `.csproj` under `src/` | 148 |
| declared modules whose closure contains ANOTHER package's declared module | **18 of 37** |
| declared modules that are RIDDEN by at least one other bundle | **4 of 37** — `MeshWeaver.Markdown.Collaboration` (by 14), `MeshWeaver.AI` (13), `MeshWeaver.Maps` (4), `MeshWeaver.Import` (1) |

The 2026-09-03 table below read 19 and 12 for the same two rows; the reference graph moved, the
shape did not. **This is the upper bound, not the answer** — it counts what the compiler sees, and
the packer then subtracts what the image ships.

### 2. What is actually PACKED — the witness decides, and for the pair #3221 names it says "no"

Same run (`34563887604`, `success`), read off the pack jobs rather than the source tree:

* `MeshWeaver.AI` (job `103155487545`):
  `closure: MeshWeaver.Markdown.Collaboration.dll does NOT ride — the platform ships it (measured off …/platform-refs)`.
  **The exact pair this issue is about — Essentials' declared module riding the AI bundle — no
  longer rides at all**, because Plugins#1515 seeds it into the image and
  [The Platform-Shipped Witness](../PlatformShippedWitness) measures that rather than trusting a list.
* `MeshWeaver.Northwind.Application` (job `103155487593`):
  `closure: MeshWeaver.Maps.dll RIDES — module-owned` and
  `closure: MeshWeaver.Import.dll RIDES — module-owned`, with
  `platform-shipped: measured against … 54 MeshWeaver.* assembl(y|ies) shipped by that host, 0 dropped from this bundle's closure`.
  **`MeshWeaver.Maps` is declared by `Maps` and `MeshWeaver.Import` by `Import`** — so the shape the
  question asks about is LIVE today, in one bundle of the 37, on two names.

So the honest statement of current behaviour is neither "it always rides" nor "it never does": the
ride survives exactly where the image supplies nothing, which is the predicate argument 3 below was
always really about.

### 3. What it costs — and why the surviving ride is safe

`MeshWeaver.Plugins` publish-bake (job `103160836446`, 05:43:41Z) and its gate shard
(`103159059393`), and `MeshWeaver.Crm` publish-bake (run `34567369849`, job `103166073109`,
06:11:35Z) — the last composing `ai.module.nupkg` and `essentials.module.nupkg` side by side, which
is the pair the 2026-09-08 hold named:

```text
module set: 5 MeshWeaver.* assembly file(s) across 5 bundle(s), 5 distinct name(s),
            0 carried by more than one bundle, 0 carried at more than one BUILD
module set: 4 MeshWeaver.* assembly file(s) across 4 bundle(s), 4 distinct name(s),
            0 carried by more than one bundle, 0 carried at more than one BUILD
```

Against that, the costs, each stated as it stands today:

* **The adoption decline is GONE for same-version copies.** A module entry of a dependency record is
  a floor (`min:<version>`), compared through the one `CompiledDependencies.Satisfies`
  ([The Dependency Record Floor](../DependencyRecordFloor)). A rebuilt copy at the same version
  satisfies a stamped floor; only a copy genuinely BELOW it declines (`FloorNotMet`).
* **The loader coin toss SURVIVES, and is now the whole of the harm.** Two copies under one simple
  name are one assembly identity; the loser's bytes are never in memory and its callers silently run
  the winner. That is untouched by any record change — which is why #3962 rewrote the guard's own
  message to stop citing the decline and cite this.
* **A floor is offered only for names an instance REGISTERS.** `MeshBuilder` adds one
  `InstalledModuleAssembly` per installed module ENTRY, so `ModuleVersionsOf` / `ModuleMvidsOf` see
  declared entries only and a ride-only name keeps the pre-#3934 `mvid:` pin (`Satisfies` refuses to
  compare across schemes). That asymmetry cuts IN FAVOUR of the case this issue asks about: a
  sibling that is another package's declared module is exactly the one a floor covers.
* **Nothing else is relaxed** — `!toolchain`, the framework identity and the content key stay
  ordinal.

### 🚨 The cycle argument does NOT cover the one ride that is still live — and it still rides

The exclusion was rejected below because resolving a declared module from its owner is a package
cycle. That is true of `AI` → `Essentials`, and **false of the only instance left**:

```
Northwind/index.json  requires: [Store, Import, Maps]   module: MeshWeaver.Northwind.Application
Maps/index.json       requires: [Store]                 module: MeshWeaver.Maps
Import/index.json     requires: [Store]                 module: MeshWeaver.Import
```

`Northwind` already depends on both owners, so for this bundle a "declared modules do not ride" rule
would be **satisfiable**. It is still the wrong rule, for a reason that does not depend on the
package graph at all:

* **Dropping the copy does not change which bytes run.** Two copies under one simple name are one
  assembly identity, so the loader already runs exactly one of them. Removing the ride removes a
  redundant FILE, not a coin toss — the toss is decided by whichever copy is seen first either way,
  and with one build per publication both answers are the same bytes.
* **It converts a present file into a cross-module load order.** Without the ride, `Northwind`'s
  assembly resolves `MeshWeaver.Maps` from whatever generation the `Maps` module landed — and under
  [Module Adoption Policy](../ModuleAdoptionPolicy) R1 that may deliberately be an OLDER generation
  than this publication's, or, if nothing of it loads, none at all. That is the
  `ReflectionTypeLoadException` on first touch [Module Closure Accounting](../ModuleClosureAccounting)
  was written after, re-introduced for a file the bundle was already carrying correctly.
* **`requires` is a CONTENT relation, not a load guarantee.** It orders installation and it is
  authored; it does not promise that the owner's entry assembly loaded in this process on this boot.
  Making the closure depend on it would make a packaging graph load-bearing for the loader.

So the rule stays predicate-based — *ship it unless the host already does* — rather than
graph-based. Where the graph happens to permit exclusion it buys nothing and costs a load ordering.

### The verdict

**Yes, it rides — and the guard #3221 asked for exists, as a divergent-build refusal rather than an
exclusivity rule.** Exclusivity was never available: `AI` `requires [Store]` while `Essentials`,
which declares the assembly, `requires [AI, …]`, so resolving the sibling from its owner is a package
cycle (argument 2 below), and the exclusion would break an install that is supported today
(argument 3). What changed between the question and this answer is that the safety stopped being an
accident of a uniform CD wave:

1. **one compilation per publication** (#3732/#3933 — one container workspace, the `sdk` entries that
   rebuilt shared siblings moved into it),
2. **a producer refusal at the one place every copy is in one hand** (the `ext-modules` composition
   step of `node-repo-gate.yml` and `node-repo-publish-bake.yml`, denominator printed green or red),
3. **a consumer HOLD over the whole sealed set** (`PublishedBundleCatalogue` →
   `SealedModuleSet.Conflicts` → `PackageAvailabilityKind.SealedSetInconsistent`), and
4. **a record that is a floor rather than a pin** (#3934/#3946).

The producer half sees only the bundles ONE run of ONE source composed; the consumer half reads
every sealed module bundle of EVERY source under the identity (`plugins`, `crm`, `education`, …),
which is the only place a set torn ACROSS sources can be seen. That division is deliberate and is
why both exist.

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

What that costs when the copies differ is a **behaviour** hazard first: every caller compiled against
the loser silently runs the winner, and nothing anywhere reports it. Where the two builds differ in
more than their MVID — a partially updated lane, a half-landed source change, two commits reaching
two `build-workspace` calls — that is a process running code its callers were not built against.

🚨 **The adoption decline that used to be quoted here is no longer the harm, and must not be cited as
it.** Until #3934 a dependency record named an exact build, so `NodeTypeCompilationHelpers.ModuleMvidsOf`
reporting the MVID of the assembly that actually loaded declined every NodeType whose record named the
other one — *"dependency record mismatch — 'MeshWeaver.Markdown.Collaboration' built against mvid:A,
live is mvid:B"*, and the view rendered empty (the `#3175` incident). #3946 replaced the pin with a
FLOOR, so for same-version copies — the only case the composed-set guard has ever actually fired on —
that decline no longer happens. A floor still declines a live copy genuinely **below** it.

The record was therefore only ever a **proxy**: it never detected the loader coin toss, it merely
fired on the same input. Removing the proxy makes the composed-set refusal **more** load-bearing, not
less — see [Module Adoption Policy](../ModuleAdoptionPolicy) → "What the platform roll gates on",
which states the rule independently of any mechanism: two builds of one platform assembly in one
identity is a torn publication, and torn publications are refused whole.

### 🚨 The copies do NOT agree, and the reason is structural (#3732)

This page used to say the copies agree by **accident** — that `bake-scope.sh` classifies any change
under `src/` as affecting ALL modules, and that `module-build-key.py` folds each entry's whole
in-repo `ProjectReference` closure into its content address, so a sibling's change re-keys every
bundle that carries it. Both statements are true. **The conclusion drawn from them was false**, and
the gap is one word: re-keying makes the bundles **REBUILD**, and says nothing about their BYTES.

**A publication is composed from several INDEPENDENT COMPILATIONS.** On `MeshWeaver.Plugins` there
were three per push, and a sibling project was compiled once in each of them:

| compilation | what it is | which bundles it produced |
|---|---|---|
| the **floor** lane's `build-workspace` | one Roslyn workspace inside the pinned image, for the first `module-pack` call (`Essentials`, `AI`, `Maps`, `Stripe`) | the DECLARED `MeshWeaver.Markdown.Collaboration`, and its ride inside `MeshWeaver.AI` |
| the **rest** lane's `build-workspace` | a second workspace, for the other `module-pack` call's `build: container` entries | its ride inside `Mcp`, `Teams`, `Mail.MicrosoftGraph`, `Observability`, `Notifications.Channels`, and the six `AI.*` providers |
| the legacy **`sdk`** entries | `dotnet build <project> -p:Version=<that module's version>` per module, on the runner — and `-p:Version` is a GLOBAL property, so every transitively referenced sibling is rebuilt under it | its ride inside `MeshWeaver.Blazor.Chat` |

With the paired MeshWeaver.Plugins change, the publication lane has **one** container workspace for
the whole catalog. Core CD's four composed modules opt into that workspace explicitly, and
`Northwind` and `Blazor.Chat` use it instead of rebuilding their shared siblings through the SDK
path. The three rows above remain the measured failure, not the resulting topology.

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

## How the divergence is removed

**The assertions REFUSE the divergence; they do not remove the second compilation.** A repo whose
publication is composed from more than one compilation now goes RED instead of shipping — which is
the correct verdict for those bytes. There are exactly two structural ways to stay green; the first
is the implemented choice and the second remains rejected:

1. **One compilation per publication.** Every bundle of one publication is packed from ONE workspace
   build. The paired `MeshWeaver.Plugins` change passes its whole module catalog through one
   `module-pack` call;
   entries that ride in-repo MeshWeaver siblings use that call's container workspace. Three legacy
   `sdk` entries remain only because their non-MeshWeaver/private closure shapes require it, and the
   lane guard names that reasoned set explicitly. Core CD follows the same rule for the four bundles
   it composes during a platform release.
   The shared compiler supplies each project with the full output closure of its in-repository
   `ProjectReference` graph, just as the SDK does. Scheduling still follows direct edges, but a
   dependent's Roslyn reference set is transitive; otherwise a valid chain such as Northwind
   `Application → Model → Domain` builds its prerequisites and then fails to see `Domain` types.
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
   (MeshWeaver#3732). Merging the former `floor` and `rest` calls then took this assembly from TWO
   builds to **ONE**. `Northwind`, the only remaining SDK entry that rode an in-repo module sibling
   (`MeshWeaver.Maps`), moved into the same workspace in that change.
🚦 **The refusal is ALREADY LIVE in five of the six node repos — this page said the opposite, and
the opposite is what would have been planned around.** The claim here was that every node repo
consumes these lanes at a full sha, so the assertion stayed INERT until each repo's own pin bump.
Measured 2026-09-11 over every `uses: Systemorph/MeshWeaver/.github/workflows/…` line in all six
(`MeshWeaver.Plugins`, `.Education`, `.Reinsurance`, `.SocialMedia`, `.Manufacturing`, `.Crm`):
**five consume `node-repo-gate.yml` and `node-repo-publish-bake.yml` at `@main`**, so they took the
refusal the hour it merged and there is no pin to move; only `MeshWeaver.Education` pins a sha
(`67cbbe0e…`), and the verdict is absent from that file at that sha. Both directions are read off
the LOGS rather than inferred from the ref — MeshWeaver.Plugins run `34563887604` and
MeshWeaver.Crm run `34567369849` (both 2026-09-11, both `success`) print the
`module set: … carried at more than one BUILD` line, and MeshWeaver.Education's publish-bake for run
`34487980275` composes the same four bundles and prints no such line at all. So nothing has to be
sequenced for five of six; what is left is Education's pin, and until it moves that repo's
publication is the one composed set nothing judges. The 2026-09-10 prediction that `MeshWeaver.Maps`
is where a refusal would first speak has since been MEASURED and is half right: `Maps` is seeded by
nothing and does still ride (into `Northwind`), but it rides at ONE build now that both are packed
from the one workspace, so the verdict on it is green rather than red — see
[Settled](#settled-the-rule-in-its-final-form) above.

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
