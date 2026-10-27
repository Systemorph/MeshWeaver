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

🚨 **Nothing, since NuGet publication was retired.** It used to cost the project its package: core's
release lane ran `dotnet pack` solution-wide with no `IsPackable=false`, so every `src/` project
shipped to nuget.org on a version tag, while the private plugins repo delivered bundles and no
packages. That is still visible in the published versions — every wave-1 project
(`MeshWeaver.Hosting.PostgreSql`, `.Hosting.Grpc`, `.InstanceSync`, `.Speech.Contract`, `.Social`) is
frozen at `3.0.0-rc7`, the release in which it moved, while everything still in core stopped at
`rc9`.

Both the lane and the rc line it ran on are gone
([Release Process & Versioning §5](/Doc/Architecture/ReleaseProcess)): nothing in the fleet restores a
MeshWeaver package, in-mesh source compiles against the platform *image*, module bundles carry their
own closures, and satellites build inside `mw-plugin-test`. The packages on nuget.org stay listed as
history. So do not price a source move against a delivery channel that no longer exists — the
version numbers above are an artefact of when each project left, not a cost anyone still pays.

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

🚨 **What the lane did NOT change is what pins most of the remaining projects.** They were held by
consumers inside core — above all by `memex/Memex.Portal.Shared` — and no build change can release
those. Only deleting the reference can.

## The keystone: `Memex.Portal.Shared` — moved to plugins

78 files, ~17.3k lines — `Api`, `Authentication`, `Email`, `Pages`, `SelfUpdate`, `Seo`, `Settings`,
`Social`, `Storage` — that sat in **core** while all four hosts that consume it
(`Memex.Portal.Gui`, `.Distributed`, `.Monolith`, `Memex.LocalMesh`) lived in **plugins**. It is now
a SOURCE move to `MeshWeaver.Plugins/src/Memex.Portal.Shared`, with its suite at
`src/Memex.Portal.Shared.Test`; the assembly name is unchanged and it still ships in the portal image.

It alone holds 13 core `ProjectReference`s — but **most of them are not real**. Measured: it builds
clean without `Observability.Contract`, `Maps`, `ContentCollections.Indexing` and `.Indexing.Graph`.
The one candidate it genuinely couples to is **`GitSync`**, through four files —
`MemexConfiguration` plus the GitHub login, connect and webhook endpoints. That is authentication,
which is precisely why an earlier attempt to move Portal.Shared wholesale was split instead: doing
it exiles sign-in from the platform repo.

**Decided (core slimming): GitHub sign-in moves WITH Portal.Shared.** `MemexConfiguration` and
`Social/GitHub*Endpoints` live in plugins now and still call core's `MeshWeaver.GitSync` library
through a `$(MeshWeaverRoot)` project reference; `GitSync` itself stays in core for now and moves in
a later step. So sign-in is no longer the platform repo's: the platform keeps the library the
endpoints call, and plugins owns the portal surface that signs a person in.

So the keystone was narrower than it looked: it gated `GitSync`, and it never gated `Maps` or
`Indexing`.

### What the move carried with it, and what it left unguarded

- 🚨 **The `InternalsVisibleTo` grants stay in core.** `MeshWeaver.Graph`, `.Hosting`,
  `.Hosting.AspNetCore`, `.Compiler`, `.Mesh.Operations`, `.PluginCatalog` and `.Testcontainers`
  name `Memex.Portal.Shared` / `Memex.Portal.Shared.Test`, which are now assemblies of ANOTHER
  repository. That is the #2689 shape — a refactor of one of those internals can red the plugins
  trunk with no local signal — and what catches it is the required
  `Dependent suites (MeshWeaver.Plugins)` verdict, which selects the moved suite by compiled reach.
- **Two guards moved with their subject.** `IdentityReadsRunAsSystemGuard` (its whole subject was
  Portal.Shared's onboarding reads) and the `SeoResolver` half of `InlineSvgEmissionBackplateGuard`
  now run in `Memex.Portal.Shared.Test`. The platform's svg guard keeps its platform files.
- **Three core ratchets lost inventory, not coverage they could keep.**
  `ImpersonationScopeSites.allow` (16 sites in 10 files), `AwaitedMeshReadSites.allow` (its last 2)
  and `LayoutAreaDataBakeSites.allow` (1) dropped their Portal.Shared lines and budgets. The plugins
  repo runs the layout-area ratchet over `src/` already (core's script, through
  `node-repo-validate.yml`); it has **no** compiled-code impersonation-latch or awaited-mesh-read
  ratchet over `src/` (its `check-impersonation.py` scans in-mesh `Source/` only), so those 18 sites
  are now guarded by nothing until one is ported.
- **Plugins' stricter `src/` gates applied on arrival.** Every one-shot `.Subscribe` in the settings
  tabs gained an error arm (`check-subscribe-error-arm.py` is zero-tolerance there; core never
  scanned `memex/`), and `check-register-before-post.py` learned that a lone `hub` argument is the
  IMessageHub a static reader is handed, never a delivery.
- **Many of the suite's tests are about PLATFORM subjects** (PluginCatalog landing, module
  activation, search refusal, token validation) and use Portal.Shared only as the host that composes
  them. They moved whole, as the suite did; by the rule in *Splitting a test that spans the
  boundary* below they belong where their subject lives, and splitting them back into a core suite
  is the follow-up. Until then the dependent-suites verdict is what runs them on a core change.

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
| `GitSync` | 21 917 | **yes** | `PluginCatalog` (13 files), `Mesh.Operations`, PluginTester/ComboAssembler/ComboVerifier, the portal hosts in plugins, and in-mesh content in five plugin packages — see the next section | **stays** until `PluginCatalog` reaches it through a seam; then module + identity flip; auth ⇒ `Modules:Required` |
| `PluginCatalog` | 21 245 | **yes** | PluginTester, ComboAssembler/Verifier | **stays** — it *is* the plugin system |
| `Documentation` | 1 670 | no | the doc gate; settled decision | **stays** |
| `Mesh.Operations` | 4 726 | **yes** | PluginTester (Portal.Shared and LocalMesh consume it from plugins) | **stays** |

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

## Feature folders inside content-surface projects

The table above is per project. A second survey looked one level down, at feature subsystems that
sit as folders inside a content-surface assembly and whose names match something MeshWeaver.Plugins
already ships. Removing any one of them changes that assembly's reference-assembly hash, so **every
removal is an identity flip**, and the clean ones should be batched into one flip.

Measured against `Systemorph/MeshWeaver@4cc5c17fbf` and `Systemorph/MeshWeaver.Plugins@f1752c83b`.

| Folder | Lines | What Plugins has | Who reaches it | Verdict |
|---|---|---|---|---|
| `Mesh.Contract/Threading` | 4 993 | `MeshWeaver.Threading.Test`, a test project only | everything: it is `IIoPool`, `PromiseCache`, `AlcLeaseRegistry` and the teardown signal (namespace `MeshWeaver.Mesh.Threading`) | **stays** — kernel. The name only looked like a feature |
| `Mesh.Contract/Payments` | 2 222 | `MeshWeaver.Payments.Stripe`, the implementer (a **second half**, not a duplicate) | the Store package's in-mesh sources (`Store/Order`, `CouponClaim`, `Catalog`, `Maintenance`), `Hosting/Deployment`, the Stripe module | **stays** — it *is* the seam. It was put in core so commerce content compiles on a portal that mounts no payment module ([The Payment Provider Contract](../PaymentProviderContract)). As a module it would bring back the `CS0234` it was created to remove |
| `Hosting/SelfUpdate` | 790 | `MeshWeaver.SelfUpdate.Aks`, the implementer (**second half**) | `PluginCatalog.RegistryUpdateReconciler` (`SelfUpdateOptions.HostOf`, the one registry-host rule), `Deployment.Contract`, core's `Memex.Portal.ServiceDefaults` health check, `Memex.Portal.Shared` | **stays** — interfaces plus options that core code calls; the provider half has already left |
| `Graph/Apps` | 1 401 | nothing of that name | the kernel home page (`UserActivityLayoutAreas` → `HomeAppSource`), `GraphConfigurationExtensions.AddAppDirectory`, in-mesh content in `AppleMaps`/`AppleWeather`/`Edu`/`Store/Core` | **stays** — the kernel calls into it, and `AppleMaps/Maps/Source/AmapsConnect.cs` reflects on `"MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph"` by **assembly-qualified name**, which a move would break silently |
| `Graph/ControlLane` | 2 940 | nothing of that name; consumers only | image assemblies `Memex.Portal.Shared` (endpoints, settings tab, self-update hand-over) and `MeshWeaver.Hosting.Instance`; in-mesh sources in `Hosting/InstanceAction`, `Hosting/Deployment`, `Hosting/PlatformBuildInbox`, `Approvals/Elsewhere` | **stays** — see below |
| `Graph/OpenGraph` | 668 | `MeshWeaver.OgCard`, a consumer (**second half**) | `GraphConfigurationExtensions` registers it on every mesh; `Memex.Portal.Shared` (`SeoEndpoints`, `OgCardRenderer`) and the OgCard module call it | **stays for now** — see below |
| `Graph/MailCopy` | 3 379 | consumers only: the `Mail.MicrosoftGraph` and `Mail.Gmail` modules | those two modules; core's compile-time impersonation table names its types; no core `src/` caller, no in-mesh binder in either repo's content trees | **move, once its home is decided** — see below |

### Why ControlLane and OpenGraph stay

Both have **an image assembly and a module or in-mesh source** among their consumers, and those
two kinds of consumer cannot share a module:

- An image project cannot reference a module. An image copy and a registry module of the same name
  is a 409.
- In-mesh sources compile against the content surface plus the installed modules
  (`CompileReferences.ComposeWithModules`). They never see an image-only plugins assembly; no in-mesh
  source in Plugins binds `MeshWeaver.Hosting.Instance` today.

For ControlLane the only home that both `Memex.Portal.Shared` / `MeshWeaver.Hosting.Instance` and
the Hosting / Approvals sources reach is the content surface, which is where it already is.

OpenGraph is smaller and has no in-mesh binder. A SOURCE move into a plugins image project (beside
`Memex.Portal.Shared/Seo/OgCardRenderer`) would work, because a module may reference an image-shipped
plugins project (`Mail.MicrosoftGraph` references `MeshWeaver.Blazor`). But the OgCard module
would then bind the portal host assembly. The module's own declaration also says the fetcher "stays
a core singleton": it is the SSRF-guarded outbound fetch (`http(s)` only, loopback and private
addresses refused). At 668 lines that is not worth an identity flip on its own. Reconsider it if
another carve flips `MeshWeaver.Graph` anyway.

### MailCopy: movable, but it has no home yet

MailCopy is the one subsystem with **no caller in core and no in-mesh binder**. Its consumers are
two plugin modules, and the core-side ties are both data, not code:

- **The runtime caller check survives a module move.** `MailCopyAuthority` judges the immediate
  calling assembly through `InMeshImpersonationGuard`, and modules load into `ModuleLoadContext`,
  which is an authentic `IPlatformLoadContext`. So a module is platform code to the guard, exactly
  as `MeshWeaver.Graph` is today.
- **The compile-time table names its types by string.** `InMeshImpersonationReferences.Members` is
  keyed `Namespace.TypeName`, with no assembly. Keeping the namespace `MeshWeaver.Graph.MailCopy` in
  the new home keeps every entry live. The table then names plugin types from core, which is a
  core→plugins edge in the sense of [Repository Dependency Direction](../RepositoryDependencyDirection).
  Close it with a seam modules contribute to, or record it.
- **One core test spans the boundary.** `InMeshImpersonationReferencesTest` compiles snippets against
  `typeof(MeshMailCopyStore)`. The table is core's subject, so the test stays in core and its
  snippets declare stub types with the same full names. The eight `MeshWeaver.Graph.Test/MailCopy*`
  suites (2 284 lines) follow the code.
- **Persisted content is safe.** `MailCopySource` and `MailMessageCopy` are registered under
  `nameof(...)`, so the stored discriminator does not carry the assembly.

What blocks the move is **where it goes**. Every plugin package declares exactly one module, and
both consumers need this code:

1. **A new provider-neutral package** whose module carries MailCopy, required by `Mail` and `Google`.
   This is the clean shape, but a new package in MeshWeaver.Plugins needs the owner's explicit go,
   because merging it publishes it.
2. **Fold it into the `Mail` module** (`MeshWeaver.Mail.MicrosoftGraph`). `Mail.Gmail` then
   references that project, and `Google` requires `Mail`. `Mail` is pre-installed, so no portal
   installs anything new. The cost is that the Gmail module binds the Outlook module.

Until that choice is made the folder stays. Once it is made, MailCopy is the only member of this
survey that can join a batched flip.

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
3. if the moved project still ships in the image, the pack lane now MEASURES that off the pinned
   image rather than taking your word for it, so no list entry is required — but the measurement is
   only as current as the image the lane pins, and it is not armed at all in a lane that pins no
   image, so state the move in the change set anyway. (`src/platform-shipped.txt` remains as a
   declaration the lane compares against and warns about; it decides nothing. See
   [The Platform-Shipped Witness](../PlatformShippedWitness).)
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

## Deployment material: each piece goes where its consumer is

The deployment material under `memex/aspire`, `deploy/aspire` and `deploy/aks` is not one thing. Each
piece is decided by **who reads it**. The guard that pinned a piece moves with it, and nothing is
deleted in core until the receiving half has merged.

| Piece | Went to | Why there |
|---|---|---|
| `memex/aspire/Memex.Aspire.Hosting` (packed as `MeshWeaver.Aspire.Hosting.Memex`) | MeshWeaver.Plugins `src/Memex.Aspire.Hosting` | Its only in-fleet consumer, `Memex.AppHost`, already lived there and pinned core's copy through `$(MeshWeaverRoot)`; nothing in core read it. The package is published from there now (`publish-aspire-package.yml`). The guard `TheAspireHostStatesTheStorageType` became Plugins' `Memex.Hosts.Test/AspireHostStatesTheStorageTypeTest`, beside its image-side counterpart. |
| `deploy/aspire/Memex.Deploy.AppHost` | MeshWeaver.Plugins `src/Memex.Deploy.AppHost` | It builds only against the integration, so it goes where the integration goes. |
| `deploy/aks/envs/example` | Systemorph/Memex `deployments/aks/example/` | It is the template for the environment folders, and every real one already lives in `deployments/aks/<env>/` there. The secret-template guard became Memex's `scripts/check-example-secret-template.py`, in its required Build (Release). |
| `deploy/aks/infra` (AKS platform Bicep) | Systemorph/Memex `infra/platform/` | Estate provisioning already lives there (`infra/estate.bicep`, the client-estate Bicep and bootstrap). |
| `deploy/helm`, `deploy/aks/manifests`, `deploy/aks/scripts`, `deploy/aks/values.aks.yaml` | **not moved by this change** — the chart is frozen in core and moves to Systemorph/MeshWeaver.Deploy in its own change | The operator image already takes the chart from MeshWeaver.Deploy (see the hosting-operator section below). For the trade-offs, see [Chart Ownership and the Runner Pool](../ChartOwnershipAndRunnerPool), "Moving the chart into a private repository". |

🚨 **Survivors packed in another repository must be DECLARED to the retirement sweep.**
`scripts/orphaned-nuget-packages.py` derives "orphaned" as *published minus packed in this tree*.
After the Aspire integration left core, that derivation reads `MeshWeaver.Aspire.Hosting.Memex` (and
`MeshWeaver.MemexTemplate`) as orphaned, and an apply would unlist a package we still publish. Both
are therefore in `PACKED_ELSEWHERE`, which the self-test pins. A stale entry errs toward sparing a
package, which is the safe direction.

🚨 **Moving a packed project moves its publishing credential.** nuget.org Trusted Publishing
policies name a repository **and a workflow file**. The policy for core's `publish-packages.yml` does
not cover MeshWeaver.Plugins' `publish-aspire-package.yml`. The package owner has to add the new
policy, and set `vars.NUGET_TRUSTED_USER` there, before the next Aspire package release. Until then
that lane's `preflight` is red by name.

## The product demo samples

ACME, FutuRe, Northwind and PensionFund were product demos sitting in the framework's
`samples/Graph` (with their `content/` and `attachments/` folders). They left as **test content**,
not as packages: MeshWeaver.Plugins carries them at `test/samples/Graph/{Data,content,attachments}`,
laid out exactly as here. Core keeps the minimal samples its own tests and docs use — `Systemorph`,
`MathDemo`, `PythonDemo`, `TestSpace`, the users, `Type`, `Doc` and the infrastructure partitions.

**Why test content and not a package.** Every compiled consumer of these trees was already in the
plugins repo — the suites that stage `samples/Graph` into their output, and the `dotnet new` template
generator that seeds ACME — so that is where they went. A package would publish them, because a
merge to a satellite's `main` lists the package in the registry. Turning FutuRe or PensionFund into a
MeshWeaver.Reinsurance package is a decision for the owner. It also cannot be the vehicle for the
compiled suites: MeshWeaver.Plugins does not check out MeshWeaver.Reinsurance, and pointing it at
that repo would make a platform-side repo depend on a satellite. `test/` is in every package
enumerator's skip set, so nothing there is published.

**"Plugins already has Northwind" was only half true.** The plugins repo's `Northwind` Store package
is the compiled analytics application (`MeshWeaver.Northwind.Application`): the dashboard, the data
cube and the sales areas. Core's tree was a different demo. It had node-per-entity NodeTypes
(`Northwind/{Customer,Product,Supplier,Employee}`) with 91 customers, 77 products and 29 suppliers as
nodes, their data-bound views and in-mesh `Test/` areas, the reports catalog, the articles and the
documentation pages. Of all that, only the `AnalyticsCatalog` overlapped the package. So the tree
moved whole instead of being deleted as a duplicate.

**How a suite stages both trees.** `src/Directory.Build.props` defines `PluginsDemoSamplesGraph`
and `CoreDemoSamplesExclude`. A suite stages
`$(MeshWeaverRoot)/samples/Graph/**;$(PluginsDemoSamplesGraph)/**` under one `Link`, and excludes the
four trees from the platform's root. Each output file therefore has exactly one source, whether or
not the pinned platform checkout still carries its old copy. FutuRe.Test's `Compile Include`s of the
FutuRe sources point at the plugins copy too.

**What the move surfaced.** The plugins repo holds content to three ratchets that core's samples tree
never met:

- `check-data-plane-messages.py` flagged ACME's Todo status menu. It posted a `DataChangeRequest`
  with the `Todo` record to its own hub, and that write never reached the node. Driving the card's
  "Update" action in `TodoStatusChangeTest` leaves the stored status at `Pending` (measured: the
  wait for `Active` times out at 30 s). The menu now writes through
  `GetMeshNodeStream().Update(...)`, and the same test sees `Active`.
- `check-node-page-provenance.py` flagged seven landing pages registered with a bare `WithView`.
  They now use `WithNodePage`. FutuRe's group analysis lands on the framework's area catalogue, which
  is recorded as an expected unresolved area.
- `check-node-path-collisions.py --root .` refused `ACME.json` + `ACME/index.md`. Here
  `--filesystem-layout` admits that pair because the FileSystem adapter merges it; there it is
  refused. The root is now `ACME/index.md` alone, with the same name, description, category and icon.

**What CI stops compiling here.** The samples stage of core's content gate used to compile these
trees and run their in-mesh `Test/` areas. The node-repo gate reads top-level packages only, so in
the plugins repo the trees are compiled only by the suites that load them. Running core's tester over
the moved trees with the same `bake-then-gate.sh` gives `[PASS]` for all four packages: 24 of 24
types compiled, the Northwind tests passed, and there were 0 warnings. A CI lane that does the same
thing there is still owed.

The `PensionFund idempotence intermittent` entry left `.github/samples-gate.allow` with its subject.
The defect it named is not fixed. Re-installing PensionFund rewrites `Currency/{CHF,EUR,USD}` without
`decimalPlaces`, because default-suppression drops a value equal to the C# initializer. That defect is
now only reachable through the moved tree.

## Moving a tool whose IMAGE carries core bytes — the hosting operator

`deploy/aks/operator` (the `hosting-*` scripts, their behaviour suite and the Dockerfile) moved to
MeshWeaver.Plugins `deploy/hosting-operator`, beside `Hosting/InstanceAction`, the plan that emits
the commands. Three things made it different from a source move:

1. **The image is two repositories' bytes.** It bakes core's chart, db chart and drift scripts beside
   the scripts, because `hosting-deploy` installs the chart it carries. The chart stays in core. The
   new lane stages core's `deploy/` at the declared platform ref with the scripts overlaid where
   core's own build had them, so the Dockerfile's `COPY` lines are unchanged. A core chart change
   reaches `:main` through an hourly rebuild that runs only when the input fingerprint moved.
2. **The tag is a consumer contract.** `hosting-operator:main` keeps its meaning. The old immutable
   tag (`<core sha7>`) was read as a core COMMIT by the estate's aks-ops lane, which checks the
   scripts out of core at that commit. The new tag is `<plugins sha7>-<core sha7>`, and
   `<plugins sha7>-<core sha7>-<deploy sha7>` once the chart came from MeshWeaver.Deploy. So core's
   copy may only be deleted after that lane checks the scripts out of the new home. The deleting
   half waits for the estate repository as well as for the adding half. The trap was the MOVING
   tag: every record pins `:main`, which the lane had always resolved in core, so supporting the
   new immutable tags was not enough — `main` itself had to resolve to the new homes (Memex#819).
3. **The equivalence was measured, not argued.** The two images were built from the same core
   commit and compared. The `/opt/hosting` tree and `/usr/local/bin` were byte-identical, and the
   entrypoint, user and env were identical. The new lane asserts the entrypoint, the user and the
   command set on every run. Moving also exposed drift: core's hand-copied list of planned commands
   had fallen out of step with the plan. The lane in the plan's repository reads the plan instead.

The guards that covered these files moved with them: the `secrets-write-only-entry` allow entries,
and the `.gitignore` re-include for `bin/`. Their release-channel writer is now declared across
repositories (`"repo"` on a binding in `.github/release-channels.json`), which keeps
`hosting-operator:main` protected. MeshWeaver.Plugins `Hosting/HostingOperator` is the manual.

## Deployment material

Core's CD keeps **BUILD + SEAL** of the product image set (portal, migration, plugin-test, control)
and publishes the sealed set into the registry; **deploying** it is the public
[MeshWeaver.Deploy](https://github.com/Systemorph/MeshWeaver.Deploy) repository's, and the hosting
operator's image is MeshWeaver.Plugins' (the section above). The test for each item: *does it
produce or seal an image of the set, or prove the set is sound?* (stays) — or *does it put a sealed
set somewhere, or watch where it runs?* (moves). Measured on `main` at `16b9902f03`.

**There is no hand-over job: MeshWeaver.Deploy pulls.** Core dispatches to no repository (policy
`dependents-verify-on-their-side`, held by `PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository`
— Deploy consumes the platform's sets like any other dependent and gets no ledgered exception).
Deploy's `build-host.yml` reads the sealed sets on its own 15-minute `schedule` from the registry
tags `promote` and `arm` write, and turns the newest one lacking a runner into a hand-over (schema 1,
MeshWeaver.Deploy's `.github/scripts/platform-set-dispatch.py` `discover` + `claim`). A dispatch
from core was weighed and refused: it would make core name its consumer, hold a credential that
writes to it, and page for its outage — the coupling the guard forbids. The price is one schedule
tick of latency on the build host, which is outside the platform's delivery. A deploy where that
latency matters (a portal roll) takes the sanctioned event route instead: core's signed build fact
into the control instance's inbox, and memex's fan-out.

### `main-cd.yml`, job by job

| job | class | where it goes |
|---|---|---|
| `preflight`, `gate` | build + seal | stays |
| `portal-image`, `portal-image-mirror`, `image-closure-record`, `migration-image`, `plugin-test-image` | build + seal | stays |
| `control-image`, `control-acceptance`, `control-promote` | build + seal (the control image and its acceptance) | stays |
| `promote`, `published-modules`, `compat-images`, `satellite-compat-image`, `satellite-compat`, `platform-ladder-compat`, `arm` | seal (the set, its evidence, the arming) | stays |
| `publish-bake`, `verify-images`, `delivery-verdict`, `alert-on-failure` | seal + its verdict | stays |
| `notify-platform-update` | hand-over (the release event the mesh fans out) | stays: it is the platform's announcement, not a deployment |
| `build-host-image`, `build-host-latest`, `build-host-report` | **deploy** | **moved** to MeshWeaver.Deploy `build-host.yml` (`image`, `handover`, `report`), which reads the sealed sets itself; nothing replaces them here |
| `control-first`, `control-lane-report` | **deploy** (rolls the control instance onto each accepted build) | **owed**: not moved yet. It is the next candidate, read by Deploy from the registry (or the inbox event) the same way — never dispatched to it |

### Files

| core path | class | where it goes |
|---|---|---|
| `deploy/build-host/`, `.github/scripts/build-run.py`, `.github/scripts/build-host-announcement.py`, `.github/build-hosts.json`, `.github/workflows/build-host-stable.yml` | deploy | **moved** to MeshWeaver.Deploy (same paths) |
| `deploy/aks/operator/` + its image lane | deploy (operator) | **moved** to MeshWeaver.Plugins `deploy/hosting-operator` (`hosting-operator.yml`, the ONE publisher of `hosting-operator:main`) |
| `deploy/helm`, `deploy/helm-db`, `deploy/aks/{manifests,scripts,values.aks.yaml}`, `deploy/homebrew`, `chart-gate.yml`, `homebrew.yml`, `test-bundle-registry.py`, `scripts/check-config-key-coverage.py` | deploy (the chart and its tooling) | MeshWeaver.Deploy (seeded; later chart changes ported by a sync PR); the core delete half is a draft |
| `deploy/aks/envs`, `deploy/aks/infra`, `deploy/aspire`, `memex/aspire/Memex.Aspire.Hosting` | deploy | delete half in flight (draft) |
| `deploy/aca`, `deploy/compose`, `deploy/compose-ha`, `deploy/marketplace`, `memex-local.yml`, `control-always-latest.yml`, `prod-synthetic-probe.yml`, `combo-verify.yml` | deploy / operations | **owed**: no move opened yet |
| `deploy/base-images`, `deploy/{node,python,pandas}-gate`, `deploy/{ocr,whisper,s1-extract}` and their image workflows, `edge-images.yml`, `dockerhub-mirror.yml`, `base-image-acr.yml` | build (images the product or its CI runs) | stays |
| `build-follow.yml`, `build-host-publication.yml`, `build-follow.py`, `build-executor.py`, `build-artifacts-fetch.py`, `compose-gate-host.sh`, `sandbox-postgres.sh`, `module-build-key.py`, `node-repo-scope.py`, `ci-artifact-store.py` | build (the CI side of a host run; helpers the runner image READS at the set's commit) | stays |
| `src/MeshWeaver.Deployment.Contract` (`BuildHostsBlock`, `BuildHostSetKeys`, `BuildHostRetention`, `CoreTestsDigest`) | contract | stays: the receivers in MeshWeaver.Plugins and the records compile against it |

🚨 **The runner image is two repositories' bytes, like the operator's.** The runner is
MeshWeaver.Deploy's; its helpers and `meshweaver-plugin-build` are core's, fetched BY COMMIT inside
the image build (`PLATFORM_COMMIT` = the set's own commit), never vendored, so the build context is
empty. So a core change to a helper reaches the next set's runner, and a Deploy change to the runner reaches
the next set too: `mw-build-host:<version>` is written ONCE per version, because the hosts are
handed it by digest.

## Package versions follow the projects: core pins only what core restores

Moving a project out of core leaves its package pins behind in `Directory.Packages.props`, because
MeshWeaver.Plugins imports that list as its version source. Left alone, core's list keeps growing
with packages nothing in core restores. Such a pin is not harmless: Dependabot and every security
review read it as a core dependency, and a version decision about a Plugins host gets made in the
wrong repository.

**The rule.** A `PackageVersion` lives in core only when some core project restores the package, at
any depth. Anything else lives in MeshWeaver.Plugins' `src/Directory.Packages.Plugins.props` as a
FILL-IN entry, which applies only when no other list declares the id. So the Plugins half can land
first with no duplicate-pin error, and core's delete lands second.

**How to measure it.** A text search for `PackageReference` is not enough, because a pin can be
needed only transitively. Restore the whole solution and read every `obj/project.assets.json`:

- a pin with no `PackageReference` that still appears among the restored libraries is a transitive
  pin, such as `Azure.Core`, `System.ClientModel` or `OpenTelemetry.Api`, and it stays;
- a pin that appears in no core project's graph belongs to whichever repository restores it.

The first pass found 67 pins with no direct core reference. 12 were transitive and stayed. 48 moved
to Plugins at the versions core carried, with their comments, including security overrides such as
`MessagePack` and `SQLitePCLRaw.lib.e_sqlite3`.

**🚨 "Nothing in core calls it" is not the test for a DIRECT reference either.** A third-party
assembly that core's graph carries anyway is part of the platform image's `/app`, and the copy there
is the one every module binds: a module's own bundled copy never loads while the platform carries the
name, and .NET binds only to an equal or higher version. So the version core's closure resolves is a
contract with every module, even when no core line calls the package. The incident: #7262 removed
`MeshWeaver.Graph`'s `Humanizer.Core` 3.0.10 reference because nothing in that project called it. The
image's `Humanizer.dll` fell back to the 2.14 that Roslyn's Workspaces brings, and every install of
the Plugins module `MeshWeaver.Graph.Views` (built against Humanizer 3.x) was refused at boot with
`requires 'Humanizer 3.0.0.0 (this platform carries 2.14.0.0)'`. Graph.Views contributed nothing, no
course exercise page rendered, and MeshWeaver.Education's install gate went red on an unchanged
commit. The Plugins fill-in pin could not help, because no project in the image's closure references
the package. The lift now sits on `MeshWeaver.Compiler.Pipeline`, the project that brings Roslyn in,
and `PlatformCarriedAssemblyFloorsGuard` holds the floor with the same link check the module landing
uses. Before dropping a reference, also ask whether `/app` still carries the assembly and at which
version. If the answer changes, the change is a platform downgrade for every module built against it.

**Families stay whole.** A package whose family core still partly pins stays in core with the rest:
the four Orleans clustering and persistence packages, `ModelContextProtocol` and
`ModelContextProtocol.AspNetCore` (core restores `ModelContextProtocol.Core`), `Microsoft.Agents.AI`
and `Microsoft.Extensions.AI`. A family split across two files drifts on the first grouped version
bump, and Orleans has already shipped mismatched versions once that way.

**The shelf and the split families followed.** The module-library shelf (`tools/MeshWeaver.ModuleLibraries`)
was a core csproj listing libraries only Plugins modules bind. It moved to Plugins with its pins, and
so did the members of the Orleans, ModelContextProtocol and Microsoft.Extensions.AI families that only
Plugins restores: each takes its version from the sibling core still pins, so the family keeps one
version source ([Core Carries No GUI Technology](../CoreCarriesNoGuiTechnology), "The module-library
shelf"). `Octokit` left once its two conditions in [GitHub Sync](../GitHubSync) held.

**Before deleting a pin, check every satellite.** A satellite may consume a pin versionless. For
the 48, the only versionless consumers outside Plugins were two Reinsurance `legacy/` projects.
That repository's CI skips them, and they import no central list.

## Related

- [Module Build Architecture](../ModuleBuildArchitecture) — the unified build every repo follows
- [Module Versioning](../ModuleVersioning) — what you author and what the build derives
- [Plugin Build Contract](../PluginBuildContract) — what a bundle may and may not carry
