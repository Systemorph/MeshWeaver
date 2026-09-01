---
nodeType: Markdown
name: Carving Projects Out Of Core
category: Architecture
description: What is still in the platform repo, what pins it there, and the two different things "move it to plugins" can mean — a SOURCE move (the assembly still ships in the image) or a MODULE move (the registry serves it). Includes the measured survey of the 38 remaining core projects and the two defects the survey found.
icon: /static/NodeTypeIcons/code.svg
---

# Carving Projects Out Of Core

The platform repo is being emptied of everything that is not platform. This page is the map: what
is left, what actually pins each thing in place, and — the distinction that causes the most wasted
work — **which of the two different moves you are making.**

Measured against `Systemorph/MeshWeaver@98195f9b2` and `Systemorph/MeshWeaver.Plugins@79271b94`.
Re-measure before acting on it; the numbers move weekly.

🚨 **Every move leaves an edge behind, and the edge points the WRONG way.** What core still reaches
back INTO the plugin repo for — the image lanes that publish the departed portal host, the gate
whose subject stayed here while the test left — is inventoried in
[Repository Dependency Direction](../RepositoryDependencyDirection). Read it before making the next
move; a move that adds an unpinned core→plugins edge has not finished.

## The two moves are not the same move

Almost every argument about "can this leave core" is really two questions wearing one name.

| | **SOURCE move** | **MODULE move** |
|---|---|---|
| What happens | the project's source moves to `MeshWeaver.Plugins/src/` | the project becomes a registry-served plugin bundle |
| Who compiles it | the plugins repo's own hosts, into the portal image | the module-pack lane, against the image |
| Where it ends up at runtime | `/app` — exactly where it was before | `modules/<Name>/`, delivered by the registry |
| Blocked by | any consumer left in core: `src/`, `test/`, `tools/`, `memex/` | a host `ProjectReference` — an image copy plus a registry module of one name is a 409 |
| Changes delivery? | **no** | yes |

Wave 1 (storage, gRPC, SignalR, InstanceSync, Migration, LocalMesh) was entirely SOURCE moves. The
projects still ship in the image; only their source lives elsewhere. Nothing about the plugin
registry was involved.

🚨 **A source move does not need the module lane to be able to build the project, and a module move
is not blocked by the lane's limitations either way.** Conflating them is why "we cannot move X
until the build supports Y" has been said about projects the build was never going to touch.

### What a source move costs, exactly once

**Its NuGet package stops being published.** Core's release lane runs `dotnet pack` solution-wide
with no `IsPackable=false`, so every `src/` project ships to nuget.org on a version tag; the plugins
repo is private and delivers bundles, not packages. This is visible in the published versions: every
wave-1 project — `MeshWeaver.Hosting.PostgreSql`, `.Hosting.Grpc`, `.InstanceSync`,
`.Speech.Contract`, `.Social` — is frozen at **3.0.0-rc7**, the release in which it moved, while
everything still in core is at **rc9**. That is the accepted, deliberate consequence of a move, not
an accident to be repaired.

## What the container build lane did and did not unblock

The 2026-08/09 lane rebuild (the platform image *is* the compiler — see
[Module Build Architecture](../ModuleBuildArchitecture)) removed the last technical reasons a **module**
could not be built:

- **the module-libraries shelf** — a module may now bind packages the image deliberately does not
  load (Octokit, PdfPig, DocSharp, the provider SDKs), with its closure recorded rather than guessed;
- **`--prebuilt`** — a module that references another module resolves to that module's built
  assembly instead of recompiling it, so module-on-module chains are cheap;
- **the shared-framework reference set** — framework-provided packages stop failing every entry.

Together those convert `MeshWeaver.GitSync` and `MeshWeaver.ContentCollections.Indexing` from
"cannot be a module" (their third-party closure had nowhere to live) into "can be".

🚨 **What the lane did NOT change is what pins most of the remaining projects.** They are held by
consumers inside core — above all by `memex/Memex.Portal.Shared` — and no build change can release
those. Only deleting the reference can.

## The keystone: `memex/Memex.Portal.Shared`

78 files, ~17.3k lines — `Api`, `Authentication`, `Email`, `Pages`, `SelfUpdate`, `Seo`, `Settings`,
`Social`, `Storage` — and it is still in **core**, while all four hosts that consume it
(`Memex.Portal.Gui`, `.Distributed`, `.Monolith`, `Memex.LocalMesh`) live in **plugins**.

It alone holds 13 core `ProjectReference`s — but **most of them are not real**. Measured: it builds
clean without `Observability.Contract`, `Maps`, `ContentCollections.Indexing` and `.Indexing.Graph`.
The one candidate it genuinely couples to is **`GitSync`**, through four files —
`MemexConfiguration` plus the GitHub login, connect and webhook endpoints. That is authentication,
which is precisely why an earlier attempt to move Portal.Shared wholesale was split instead: doing
it exiles sign-in from the platform repo. **That contradiction is still open and is a maintainer
call, not an implementation detail.**

So the keystone is narrower than it looks: it gates `GitSync`, and it does not gate `Maps` or
`Indexing`.

The second-order pin behind it is `tools/MeshWeaver.PluginTester` — core-owned, shipped as the gate
image — which directly references `Indexing.Graph`, `GitSync`, `Maps`, `Mesh.Operations` and
`PluginCatalog`. Since it gained `--module` those references are convertible rather than fatal, but
each has to be converted deliberately.

## The real boundary is the CONTENT SURFACE

The `.csproj` graph is not what decides whether a project can leave cheaply.
`FrameworkBuildIdentity.ContentSurfaceAssemblies` is — the 26 assemblies whose reference-assembly
hashes are folded into the framework build identity `s<hash>`, and therefore the set that in-mesh
content compiles against by default.

**Both projects moved in the first wave were OUTSIDE that list, and that is the entire reason they
were free.** `MeshWeaver.Hosting.Embeddings` and `MeshWeaver.Observability.Contract` are not content
surface, so nothing in any mesh binds them by name, no bake was invalidated, and the move was a
`.csproj` edit.

🚨 **Every remaining candidate is INSIDE the content surface**, and leaving it is a different class
of operation with a precedent to copy rather than a decision to improvise. `MeshWeaver.AI` and
`MeshWeaver.Markdown.Collaboration` have both already made the trip, and the comment left in
`FrameworkBuildIdentity` states the shape exactly: the project becomes a MODULE, leaves the
identity list, is composed into content compiles per-mesh via
`CompileReferences.ComposeWithModules`, and is made `required` by a pre-installed package so every
portal still lands it.

The cost is a **framework identity flip**: a new `s<hash>`, so every existing bake is invalidated
and every portal and satellite re-bakes (a degraded start of roughly ten minutes per pod, and
satellites must republish under the new identity before their bundles are adoptable). That is a
delivery event, not a refactor, and it is why these do not get done casually or in batches of one.

## The remaining core projects, and what pins each

| Project | Lines | Content surface? | What pins it to core | Verdict |
|---|---|---|---|---|
| `Hosting.Embeddings` | 471 | no | **nothing at all** | moved out — free |
| `Observability.Contract` | 1 320 | no | a ships-the-bits reference + one test project | moved out — free |
| `Maps` | 116 | **yes** | in-mesh sample content (`Cornerstone/Pricing`), 5 test meshes, PluginTester | module + identity flip |
| `ContentCollections.Indexing` | 1 851 | **yes** | one `using` in `Mesh.Operations`; in-mesh content | module + identity flip |
| `ContentCollections.Indexing.Graph` | 2 253 | **yes** | PluginTester; in-mesh content | with the above |
| `GitSync` | 9 649 | **yes** | `PluginCatalog`, 4 real files in Portal.Shared (**GitHub login = authentication**) | module + identity flip; auth ⇒ `Modules:Required` |
| `PluginCatalog` | 21 245 | **yes** | Portal.Shared, PluginTester, ComboAssembler/Verifier | **stays** — it *is* the plugin system |
| `Documentation` | 1 670 | no | the doc gate; settled decision | **stays** |
| `Mesh.Operations` | 4 726 | **yes** | Portal.Shared, LocalMesh, PluginTester | **stays** |

Everything not listed is kernel: messaging, data, layout, the mesh contract, hosting, the compiler
and NuGet resolution, packaging.

### The `ProjectReference` graph lies about all of them

🚨 Four of the references that appear to pin these projects turned out to carry **no code
dependency at all** — `Memex.Portal.Shared` builds Release `-warnaserror` with **0 Warning(s)**
after dropping `Observability.Contract`, `Maps`, `ContentCollections.Indexing` *and*
`.Indexing.Graph`. They exist to put the assembly in the app closure, nothing more.

And the reference that actually matters is **not in any `.csproj`**:
`samples/Graph/Data/Cornerstone/Pricing/Source/PricingLayoutAreas.cs` binds `MapControl`, and it is
a `Source/*.cs` mesh node — it compiles at RUNTIME in the portal, never in CI. That is why five core
test projects reference `MeshWeaver.Maps` while not one line of their own C# mentions it: their test
meshes compile that sample.

**So neither direction of the `.csproj` graph can be trusted here.** A reference may be dead weight,
and a live consumer may have no reference at all. Check both: a warnings-as-errors build of the
consumer settles the first; the content trees and the node JSON settle the second.

## Two defects this survey found

Both were the *same shape*: a move that was left half-finished, where the leftover looks exactly
like a deliberate decision.

### `Hosting.Embeddings` had no consumers at all

It was in `MeshWeaver.slnx`, built by core CI and published to nuget.org — and **nothing in core
referenced it.** No `.csproj`, anywhere in `src/`, `test/`, `tools/` or `memex/`. The only four
mentions of the name in the whole repo were prose inside comments. Its three real consumers —
`MeshWeaver.AI.AzureFoundry`, `MeshWeaver.Hosting.PostgreSql`, `MeshWeaver.Hosting.Snowflake` — were
all in plugins already; it had simply not travelled with the storage backends in wave 1.

🚨 **A project with zero consumers does not announce itself.** It compiles, it packs, it publishes,
and every CI signal stays green. Grep the *`.csproj` graph*, not the source text — the four textual
mentions here would have read as "still in use" to any search that did not distinguish a reference
from a sentence.

### `Observability.Contract` existed twice, and had begun to drift

Both repos carried it — **all twelve source files byte-identical**, only the `.csproj` differing.
Both fed the *same portal image*: `Memex.Portal.Gui` (plugins) referenced the plugins copy while
`Memex.Portal.Shared` (core) referenced core's, and Portal.Gui references Portal.Shared. Two
projects, one assembly name, one publish — precisely the same-identity duplicate that
`src/platform-shipped.txt` exists to prevent, and which that file could not prevent because it
assumes the name occurs once.

The tests had duplicated too, and had already diverged: `UndiagnosableReportTest` existed in both
with the same 15 test names but 56 differing lines, the plugins copy strictly stronger (it exercised
five real `LogPipelineGap` findings where core's exercised one, and asserted the fingerprint against
the real report rather than a literal).

**The defect was documented in the tree, in a comment, by whoever left it:**

> *"the portal image builds BOTH — so the predicate has to exist in each, or the type is missing at
> runtime whenever the other copy wins the copy-local step."*

🚨 **A hazard written down in a comment is not a mitigated hazard.** That sentence describes a coin
toss decided by copy-local ordering, and it sat in the file it describes, being true, for as long as
both copies existed.

## The rule that generalises from both

**A cross-repo move is finished only when the source is DELETED from the origin.** Until then the
two copies are indistinguishable from a deliberate contract split, and the tell — a duplicated
assembly name, or a project nothing references — is invisible to every gate the repo has: both
halves compile, both are packed, and CI is green on all of it.

When you move a project, in the same change set:

1. delete it from the origin repo and from `MeshWeaver.slnx`;
2. remove every `ProjectReference` that pointed at the origin copy — including the ones that exist
   only to ship the bits, which are the easiest to mistake for a real dependency (a
   warnings-as-errors build of the consumer will tell you: if it still compiles clean, there was no
   code dependency);
3. if the moved project still ships in the image, add it to `src/platform-shipped.txt` in plugins,
   or its bundles will carry a second copy beside `/app`'s;
4. add it to the plugins host build list in `ci.yml` — it is explicit, not a glob, exactly so a
   moved project cannot silently stop being built;
5. move its tests, and **split any test that spans the boundary** rather than moving it whole —
   see below.

## Splitting a test that spans the boundary

`CompileFailureReportOrderTest` pinned one invariant with two halves: the platform's compile-failure
report is *ordered* so the verdict survives truncation (a property of `CompileDiagnostics`), and
that verdict *does* survive the watcher's real burst aggregation (which needs the observability
contract). When the contract left, the test had a foot on each side of the boundary.

Three ways to resolve that, two of them wrong:

- **Move it whole.** It calls `CompileDiagnostics.FormatCompileFailureReport` and
  `EmitPipeline.EmitCompilationToDirectory`, both `internal`. Following the contract would have
  meant an `InternalsVisibleTo` from core to a *plugins test assembly* — a consumer core's own CI
  never builds, so an ordinary refactor of an internal method reds another repo's trunk with no
  local signal. That is issue #2689's exact shape; do not add instances of it.
- **Delete the end-to-end assertion** and lean on the two halves. Wrong for a subtler reason than
  it first appears. The obvious argument for keeping it — that the console formatter's six-space
  per-line indent is charged against the same budget and could push the verdict out — does not
  survive measurement: with the ordering fix in place the verdict renders at index **299** of 2000,
  because it leads. What the assertion is really worth is that it states the invariant in **the
  units the incident is filed in**. The ordering tests say "the verdict precedes the context"; this
  one says "the ticket names a `CS####`", which is the thing that was actually broken.
- **Reformulate so each assertion sits with its subject.** What was actually done. The composition
  property is a property of *the report* — is its verdict early enough to survive rendering — so it
  is measured in core on the console-rendered text, with no observability type. What the watcher
  does once a burst is over budget is the *watcher's* property and was already pinned next to the
  aggregator in plugins. Neither side reaches across the boundary.

🚨 **When a test spans a repo boundary, ask which repo's CI should go red when the subject
regresses** — and put each assertion there. A test that follows its *instrument* rather than its
*subject* leaves the subject uncovered where it lives, and usually needs a visibility hole to
compile once it arrives. If it will not split, that is a signal the seam is in the wrong place —
not a licence to widen `InternalsVisibleTo`.

## Moving a SAMPLE SPACE into a package

Content moves differently from code, and the difference is not obvious until it bites.

A sample partition in `samples/Graph/Data/<Name>` is a **disk-loaded Space**: its `index.md` carries
`NodeType: Space` front matter and `AddPartitionedFileSystemPersistence` reads it off the file
system. A plugin package is not that. **No package in `MeshWeaver.Plugins` ships a Space** — every
one of the forty-odd package roots is `Store/Plugin`, and a plugin's partition is created by the
`Store/Provision` node plus `SystemInstall`, with content arriving through that space's `_GitSync`.

So the conversion, not a copy:

1. **`index.md` becomes `index.json`.** The front matter (name, category, description, icon) folds
   into the package declaration and the markdown body becomes `content.body`. Leaving both in place
   is not an option — `index.md` and `index.json` would each claim the package's own path.
2. **Declare what the content binds.** Cornerstone's RiskMap view binds `MapControl`, so the package
   `requires` Maps. A content package's `requires` is not decoration; it is what makes the in-mesh
   compile resolve.
3. **🚨 Leave `_Access` behind.** A sample tree carries `AccessAssignment` nodes for its demo users.
   Shipping those in a package injects named-user grants into *every mesh that installs it*, and no
   other package does this — a synced space's access comes from provisioning. Tests get their grants
   from the shared test-user fixture instead. See the `plugin-provisioning` skill: hand-creating a
   Space grants its creator Admin, and every human-run `git_hub_sync` re-mints that grant, so the
   access shape is a security decision rather than a detail.
4. **The tests follow the CONTENT, not the subsystem.** Cornerstone's suites came from three
   different platform test projects and became one project in the plugins repo, because what they
   share is the partition they load. Tests that merely *mention* the content in a comment stay
   where they are — moving them exports unrelated platform coverage.
5. **Staging becomes two globs.** A suite that used to copy `samples/Graph/**` now assembles its
   fixture from two repos: the platform's tree for the sibling partitions, this repo's for the moved
   one. And any `Add<Name>()` helper in `SampleDataExtensions` leaves with the content — the moved
   suites call `IncludePartition("<Name>")`, the primitive it wrapped.

## Related

- [Module Build Architecture](../ModuleBuildArchitecture) — the unified build every repo follows
- [Module Versioning](../ModuleVersioning) — what you author and what the build derives
- [Plugin Build Contract](../PluginBuildContract) — what a bundle may and may not carry
