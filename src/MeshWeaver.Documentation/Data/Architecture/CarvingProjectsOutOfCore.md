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
  `.Hosting.AspNetCore`, `.Compiler`, `.Mesh.Operations` and `.PluginCatalog` (and `.Testcontainers`
  until it followed the suite to plugins, below) name `Memex.Portal.Shared` / `Memex.Portal.Shared.Test`, which are now assemblies of ANOTHER
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
`FrameworkBuildIdentity.ContentSurfaceAssemblies` is. It lists the 26 assemblies that are the
closure of the content gate (`tools/MeshWeaver.PluginTester`). That makes it the set shipped content
is PROVEN to compile against: the bake host compiles against that closure plus the modules it
lands, so content that binds anything else does not pass the gate. Their reference-assembly hashes
make up the surface identity `s<hash>`.

🚨 **The surface identity is no longer the adoption key.** Adoption keys on
`FrameworkBuildIdentity.FrameworkVersion`, which is the compatibility key `c<major>e<epoch>`
(`PlatformCompatibility.KeyOf`, policy `platform-backwards-compatibility`). The `s<hash>` is
`FrameworkBuildIdentity.BuildProvenance`: it is logged and recorded, and it never causes a cache
miss. So a change to a content-surface assembly's public surface does **not** by itself invalidate
any bake or re-bake any portal. Earlier versions of this page said it did. That was true while the
surface hash was the key, and it no longer is.

**What removing a public type IS: a binary break**, judged by
[The Platform Compatibility Ladder](../PlatformCompatibilityLadder). Its pull-request check links the
DEPLOYED plugin set against the candidate platform, and its break catalogue
(`ModulePlatformMemberLinkTest`) pins what each kind of change does to a compiled plugin. A removal
that a published plugin still binds is red, and it has exactly three answers:

1. **Keep it compatible**: an `[Obsolete]` forwarder under the old signature, or a
   `[TypeForwardedTo]` to an assembly the platform still ships. Core cannot forward to a plugins
   assembly, so a type that leaves for a module cannot use the second form.
2. **No published plugin binds it any more**: the plugin side moves to the new home and republishes
   FIRST, and the core removal lands after. This is the deleting-half-last order that
   `Pairs-with:` already enforces.
3. **A DECLARED epoch bump**: `"epoch"` plus a `"breaks"` entry in
   `src/MeshWeaver.Compiler/platform-compatibility.json`. This is the real fleet-wide event: a new
   compatibility key, so every plugin must rebuild before its bytes are adopted again. It is a
   delivery event for the owner to schedule. Never treat it as a side effect of a refactor.

🚨 **The ladder cannot see reflection or string-named binding** (`Type.GetType("…")`). A removal
reached that way links green and fails at runtime, so search for it by hand (see `Graph/Apps`
below).

🚨 **The ladder links every module the sealed `plugins` publication carries — and nothing that
ships outside it.** Until MeshWeaver#7409 its input was a fixed list of four modules; it is now the
whole publication, proven against the publication's own index (see
[Platform Compatibility Ladder](../PlatformCompatibilityLadder)). Two consumers are still outside it:
`Memex.Portal.Shared`, which ships in the image rather than as a module, and any satellite's modules.
A carve that removes types those bind needs its own compatibility check of them:
- build each one from source against the candidate core, with Release `-warnaserror`;
- or, better, link its published bytes against the candidate.

Answer 2 above is then shown by that check, not by the gate. (Measured on the pull-request lane,
`dotnet-test.yml`. Whether the promoted-image rung in `main-cd.yml` reads a wider set was not
measured here.)

**Both projects moved in the first wave were OUTSIDE the content surface, and that is why they
were free.** Nothing in any mesh bound `MeshWeaver.Hosting.Embeddings` or
`MeshWeaver.Observability.Contract`, so the move was a `.csproj` edit.

🚨 **Every remaining candidate is INSIDE the content surface**, and leaving it means copying a
precedent rather than improvising. `MeshWeaver.AI` and `MeshWeaver.Markdown.Collaboration` have both
already made the trip, and the comment in `FrameworkBuildIdentity` states the shape: the project
becomes a MODULE, leaves the list, is composed into content compiles per-mesh via
`CompileReferences.ComposeWithModules`, and is made `required` by a pre-installed package so every
portal still lands it. What that costs is the ladder's question above: which published plugins bind
the departing types, and which of the three answers each of them gets.

## The remaining core projects, and what pins each

| Project | Lines | Content surface? | What pins it to core | Verdict |
|---|---|---|---|---|
| `Hosting.Embeddings` | 471 | no | **nothing at all** | moved out — free |
| `Observability.Contract` | 1 320 | no | a ships-the-bits reference + one test project | moved out — free |
| `Maps` | 116 | **yes** | in-mesh sample content (`Cornerstone/Pricing`), 5 test meshes, PluginTester | module; answer the ladder (see above) |
| `ContentCollections.Indexing` | 1 851 | **yes** | one `using` in `Mesh.Operations`; in-mesh content | module; answer the ladder (see above) |
| `ContentCollections.Indexing.Graph` | 2 253 | **yes** | PluginTester; in-mesh content | with the above |
| `GitSync` | 21 917 | **yes** | `PluginCatalog` (13 files), `Mesh.Operations`, PluginTester/ComboAssembler/ComboVerifier, the portal hosts in plugins, and in-mesh content in five plugin packages — see the next section | **stays** until `PluginCatalog` reaches it through a seam; then module, answering the ladder; auth ⇒ `Modules:Required` |
| `PluginCatalog` | 21 245 | **yes** | PluginTester, ComboAssembler/Verifier | **stays** — it *is* the plugin system |
| `Documentation` | 1 670 | no | the doc gate; settled decision | **stays** |
| `Mesh.Operations` | 4 726 | **yes** | PluginTester (Portal.Shared and LocalMesh consume it from plugins) | **stays** |
| `Testcontainers` | 385 | no | nothing: its two consumers (`Memex.Portal.Shared.Test`, `MeshWeaver.Testcontainers.Test`) were already in plugins | moved out — free |
| `Hosting.AspNetCore` | 3 970 | no | a satellite's compiled module (the endpoint hook); seven Plugins modules' published bytes (`PortalApplication` and three more) | **split** — the host-only half of `Portal/` moved out (see below); the rest stays |
| `Markdown.Export.Contract` | 355 | no | `PortalApplication`, which stays | **stays** |

Everything not listed is kernel: messaging, data, layout, the mesh contract, hosting, the compiler
and NuGet resolution, packaging.

### Outside the content surface is not the same as free

The content-surface test answers "does a move flip the framework identity". It does not answer "who
else compiles against this from source". `Hosting.AspNetCore` is outside the surface, and no project
in core's `src/`, `test/` or `tools/` references it, so it looked free. Two consumers say otherwise:

- `memex/aspire/Memex.Portal.ServiceDefaults` references it. That project is itself leaving core
  (Systemorph/MeshWeaver#7297, Plugins#4333), so this pin goes away on its own.
- **A satellite references it.** MeshWeaver.SocialMedia's compiled module `MeshWeaver.Social` declares
  its HTTP endpoints through `MeshEndpointProviderAttribute`, the endpoint-contribution hook (design
  #1655), and compiles it from `$(MeshWeaverRoot)/src/MeshWeaver.Hosting.AspNetCore`. Satellites
  depend on the platform and on nothing else (see
  [Repository Dependency Direction](../RepositoryDependencyDirection)); none checks out
  MeshWeaver.Plugins. Moving the project would leave that module with no source to compile against.

That is why `Hosting.AspNetCore` was split rather than moved (see "Splitting `Hosting.AspNetCore`"
below): the hook (`MeshEndpointProviderAttribute`, `ModuleEndpointDataSource`, the endpoint and static-asset
extensions) is a platform extension point, and `Portal/` is host code. That split changes which
assembly declares public types, so it needs type forwarders and is a design change, not a move.
`Markdown.Export.Contract` is pinned by `Hosting.AspNetCore` alone (`PortalApplication`), so it waits
with it. 🚨 **Before calling any move free, grep the satellites' `.csproj` files for
`$(MeshWeaverRoot)/src/<Project>`**, not only core's.

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

## Splitting `Hosting.AspNetCore`: what published bytes bind decides what stays

`MeshWeaver.Hosting.AspNetCore` is outside the content surface, but it is not free to move. The
satellite MeshWeaver.SocialMedia compiles its module `MeshWeaver.Social` against it from core source,
because that module declares its endpoints with `MeshEndpointProviderAttribute`. So the project was
split instead of moved, and the split followed one test: **can every published plugin or satellite
byte still link?**

A `[TypeForwardedTo]` only points toward an assembly the forwarding assembly references, and core
never references MeshWeaver.Plugins. So a type that leaves core for Plugins gets no forwarder, and it
may leave only if no published binary binds it by assembly name. The answer was MEASURED, not
grepped. Every registry module of MeshWeaver.Plugins (42 module DLLs) was built, and the `TypeRef`s it
holds into `MeshWeaver.Hosting.AspNetCore` were read from the metadata:

| Bound by published module bytes | Modules |
|---|---|
| `MeshEndpointProviderAttribute` | Courses, Mail.Gmail, Mail.MicrosoftGraph, Mcp, Teams, WhatsApp (and the satellite's `MeshWeaver.Social`) |
| `McpConfiguration`, `SessionHubResolver` | Mcp |
| `Portal.PortalApplication` | AppleMessages, Blazor.Chat, Courses, Mail.Gmail, Mail.MicrosoftGraph, Teams, WhatsApp |
| `Portal.PortalErrorSink` | Blazor.Chat |
| `Portal.UserIdentityCache`, `Portal.UserIdentityLookup` | Mail.MicrosoftGraph |

Those types stay, together with what their public signatures name (`ICircuitContextAccessor`,
`UserDirectoryReading`, `PortalErrorReporting`) and the one package `PortalApplication` needs,
`MeshWeaver.Markdown.Export.Contract`. The text search for the namespace finds more. Courses and Mcp
mention `UserContextMiddleware` in comments, for instance. But a comment is not a `TypeRef`, so a text
search over-counts.

**What moved** to MeshWeaver.Plugins as `MeshWeaver.Hosting.AspNetCore.PortalHost`, under that NEW
namespace, is host code that no published binary binds: `UserContextMiddleware`, `ValidatedTokenCache`,
`MeshUserProjection`, `VUserHelper`, `UserDirectoryReadiness`, `RequestNamingExceptionHandling` and
`RequestName`, `NonfileRouteConstraint` and its extensions, `ArticleExtensions`, and
`Authentication/AuthenticationOptions` with its two companions. Their consumers are the portal hosts
and the Blazor host libraries, which are rebuilt with every image. `scripts/type-forwards.allow` names
each one, and every entry is a statement this measurement backs.

**The transition: a new namespace, because the module build never sees a checkout.** The first
attempt kept the namespace and switched on the pinned platform CHECKOUT: while it still carried
`Portal/UserContextMiddleware.cs`, the Plugins copy compiled nothing. The module bundles'
one-workspace build defeated it. That build (`mw-plugin-test build-project` inside the sealed portal
image) compiles against the image's `/app` and has no platform checkout, so the switch read "moved"
and compiled the copy next to an assembly still exporting the same full names: CS0436 on every type.
Reproduced locally with the tester inside a portal image that predates the delete: 6 CS0436, the
same as CI.

A switch on a path cannot answer a question about an assembly, so the moved types took a new
namespace, `MeshWeaver.Hosting.AspNetCore.PortalHost`. That copy never shares a full name with the
platform's assembly, whichever platform set a build resolves. The consumers switch in the same change.
A file that also uses a type the platform kept aliases each moved type it uses, so the simple name is
never ambiguous while the platform still carries its copy. The platform then deletes its copy with no
consumer left. The same 39-entry workspace build is GREEN, 52 of 52 projects, against the same
image. **When a moved type's old full name stays exported anywhere a build path can see it, prefer a
new namespace to any switch.**

**What it does not cover.** The measurement covers the registry modules MeshWeaver.Plugins builds
today, plus a source search of every Systemorph repository for the namespace (only
MeshWeaver.SocialMedia uses it, and only the endpoint hook). A module that some other estate built and
published from a private fork would not be seen.

## Feature folders inside content-surface projects

The table above is per project. A second survey looked one level down, at feature subsystems that
sit as folders inside a content-surface assembly and whose names match something MeshWeaver.Plugins
already ships. Removing any one of them removes public types from a content-surface assembly. Under
the ladder above, that is a **binary break for every published plugin that still binds them**. The
`s<hash>` changes too, but only as provenance. So the order is: the plugin side moves and
republishes first, the core removal lands after, and an epoch bump is only for a break that cannot
be answered any other way. Batching is still worth it, but the reason is different. If moves must
end in a declared break, one bump costs the fleet one rebuild instead of several.

Measured against `Systemorph/MeshWeaver@4cc5c17fbf` and `Systemorph/MeshWeaver.Plugins@f1752c83b`.

| Folder | Lines | What Plugins has | Who reaches it | Verdict |
|---|---|---|---|---|
| `Mesh.Contract/Threading` | 4 993 | `MeshWeaver.Threading.Test`, a test project only | everything: it is `IIoPool`, `PromiseCache`, `AlcLeaseRegistry` and the teardown signal (namespace `MeshWeaver.Mesh.Threading`) | **stays** — kernel. The name only looked like a feature |
| `Mesh.Contract/Payments` | 2 222 | `MeshWeaver.Payments.Stripe`, the implementer (a **second half**, not a duplicate) | the Store package's in-mesh sources (`Store/Order`, `CouponClaim`, `Catalog`, `Maintenance`), `Hosting/Deployment`, the Stripe module | **stays** — it *is* the seam. It was put in core so commerce content compiles on a portal that mounts no payment module ([The Payment Provider Contract](../PaymentProviderContract)). As a module it would bring back the `CS0234` it was created to remove |
| `Hosting/SelfUpdate` | 790 | `MeshWeaver.SelfUpdate.Aks`, the implementer (**second half**) | `PluginCatalog.RegistryUpdateReconciler` (`SelfUpdateOptions.HostOf`, the one registry-host rule), `Deployment.Contract`, core's `Memex.Portal.ServiceDefaults` health check, `Memex.Portal.Shared` | **stays** — interfaces plus options that core code calls; the provider half has already left |
| `Graph/Apps` | 1 401 | nothing of that name | the kernel home page (`UserActivityLayoutAreas` → `HomeAppSource`), `GraphConfigurationExtensions.AddAppDirectory`, in-mesh content in `AppleMaps`/`AppleWeather`/`Edu`/`Store/Core` | **stays** — the kernel calls into it, and `AppleMaps/Maps/Source/AmapsConnect.cs` reflects on `"MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph"` by **assembly-qualified name**, which a move would break silently |
| `Graph/ControlLane` | 2 940 | nothing of that name; consumers only | image assemblies `Memex.Portal.Shared` (endpoints, settings tab, self-update hand-over) and `MeshWeaver.Hosting.Instance`; in-mesh sources in `Hosting/InstanceAction`, `Hosting/Deployment`, `Hosting/PlatformBuildInbox`, `Approvals/Elsewhere` | **stays** — see below |
| `Graph/OpenGraph` | 668 | `MeshWeaver.OgCard`, a consumer (**second half**) | `GraphConfigurationExtensions` registers it on every mesh; `Memex.Portal.Shared` (`SeoEndpoints`, `OgCardRenderer`) and the OgCard module call it | **stays for now** — see below |
| `Graph/MailCopy` | 3 379 | consumers only: the `Mail.MicrosoftGraph` and `Mail.Gmail` modules | those two modules; core's compile-time impersonation table names its types; no core `src/` caller, no in-mesh binder in either repo's content trees | **moved** into the `Mail` module, namespace `MeshWeaver.Mail.MailCopy`; `Google` requires `Mail` — see below |
| `GitSync` Azure Repos provider | ~1 000 | `MeshWeaver.GitSync.AzureRepos` (+ `.Test`), the implementer (**second half**) | GitSync's own sync, pull-request and issue services, through the `IGitHostingProvider` seam — never by name; `Azure.Identity` — the only Azure package a core `src/` project referenced; no in-mesh binder in either repo's content trees | **moved** — the seam (`IGitHostingProvider`, `GitHostingProviders`) stays in core; the provider is a MeshWeaver.Plugins project the portal composition registers (`AddAzureReposGitHosting`). It left in three steps: the seam with Azure registered as a built-in; the module taking the kind over (a contributed provider replaces a built-in of its kind); the platform deleting its copy and the `Azure.Identity` reference. The central `Azure.Identity` pin stays: Plugins projects consume it versionless. See [Syncing a Space with GitHub](../GitHubSync) → *Hosting providers* |

### Why ControlLane and OpenGraph stay

Both have **an image assembly and a module or in-mesh source** among their consumers. Two
questions about a new home are easy to conflate. Keep them apart:

- **Compiler visibility** is per host. `CompileReferences.GetDefaultReferences()` takes the host's
  whole `TRUSTED_PLATFORM_ASSEMBLIES`, and `ComposeWithModules` adds the installed modules on top
  without filtering down to the content surface. So on a PORTAL, an in-mesh source CAN see an
  image-shipped plugins assembly such as `Memex.Portal.Shared`.
- **Being proven** is the bake host's question. The content gate compiles against ITS closure, which
  is the content surface plus the modules it lands, and that closure has no portal-only assemblies.
  A source that binds an image-only plugins assembly can therefore compile on the portal and still
  fail the gate.

Identity membership is a third question, and since the compatibility key it decides nothing about
adoption (see above).

So for each candidate home, ask whether BOTH the portal's references and the bake host's references
carry it:

- **A module**: both hosts see it, but an image project cannot reference a module. An image copy
  and a registry module of the same name is a 409. `Memex.Portal.Shared` binds ControlLane (its
  endpoints, settings tab and self-update hand-over), so ControlLane cannot be a module.
- **An image-only plugins assembly**: `Memex.Portal.Shared` is fine, and the in-mesh
  Hosting / Approvals sources would compile on a portal. But the gate's bake host would not see it,
  so those sources would no longer be proven.

Neither home satisfies all of ControlLane's consumers, so it stays in the content surface. Not
established: `MeshWeaver.Hosting.Instance` is described both as shipping in the image and as the
`Hosting.Instance` package's module. Whether the bake host lands it, which would make it a possible
home, needs a trial bake, not a reading.

OpenGraph is smaller and has no in-mesh binder. A SOURCE move into a plugins image project (beside
`Memex.Portal.Shared/Seo/OgCardRenderer`) would work, because a module may reference an image-shipped
plugins project (`Mail.MicrosoftGraph` references `MeshWeaver.Blazor`). But the OgCard module
would then bind the portal host assembly. The module's own declaration also says the fetcher "stays
a core singleton": it is the SSRF-guarded outbound fetch (`http(s)` only, loopback and private
addresses refused). Its two callers are both published plugin bytes (the OgCard module, and
`Memex.Portal.Shared` in the image), so the move would need answer 2 of the ladder for each, for
668 lines. Reconsider it when another carve out of `MeshWeaver.Graph` is sequencing the same
republish anyway.

### MailCopy: moved into the Mail module

MailCopy was the one subsystem with **no caller in core and no in-mesh binder**. Its consumers were
two plugin modules, and the core-side ties were both data, not code.

**Where it went.** Every plugin package declares exactly one module, and both consumers need this
code. Two homes were on the table: a new provider-neutral package required by `Mail` and `Google`,
or the existing `Mail` module. The `Mail` module was chosen. The code now lives in
`MeshWeaver.Mail.MicrosoftGraph` (folder `MailCopy/`, namespace `MeshWeaver.Mail.MailCopy`), the
Gmail module references that project, and `Google/index.json` declares `Mail@^1.25.0` in `requires`.
`Mail` is pre-installed, so no portal installs anything new. The cost is that the Gmail module's
bundle carries the Outlook module's closure. At run time Gmail is served the Mail module's own copy
of the assembly (`ModuleLoadContext`: another module's entry assembly), so every MailCopy type has
one identity. The design page moved with the code: MeshWeaver.Plugins `Mail/MailCopy`.

**How it moved: add first, delete second.** The Plugins half added the code under the NEW
namespace while core still carried the old one. Two assemblies with one full type name would have
made every Gmail and Outlook file that names a MailCopy type ambiguous (`CS0433`) against a core
that still had it. With a new namespace the Plugins half compiles against core before and after
this deletion, so neither half waits on the other's build. Core's half then deletes the folder.

**The ties, and what became of each:**

- **The runtime caller check survives the move.** `MailCopyAuthority` judges the immediate calling
  assembly through `InMeshImpersonationGuard`, and modules load into `ModuleLoadContext`, which is an
  authentic `IPlatformLoadContext`. So a module is platform code to the guard, exactly as
  `MeshWeaver.Graph` was.
- **The compile-time table moves to the new names, and never goes empty.** `InMeshImpersonationReferences.Members`
  is keyed `Namespace.TypeName`, with no assembly. The module's names were registered BESIDE core's own
  first (Systemorph/MeshWeaver#7313), and the module move required that platform. Only then does this
  deletion drop the `MeshWeaver.Graph.MailCopy` keys. The nine `MeshWeaver.Mail.MailCopy.*` keys stay, so the check fires on the moved types in every in-mesh compile that
  references the Mail module, and names nothing where it is not installed. That is a core→plugins
  edge by NAME ([Repository Dependency Direction](../RepositoryDependencyDirection)), and it is
  RECORDED here rather than closed. Closing it needs a seam a module can contribute to. The obvious
  seam, reading `[ImpersonationSurface]` off referenced metadata, would cost a semantic query per
  member name in every NodeType compile, because the scan's cheap name prefilter could no longer
  know the names in advance. Renaming the namespace in Plugins would silence the entries without a
  sound, so the drift check moved to the side that can see both: Plugins'
  `MailCopySurfaceRegistrationTest` fails when a public `[ImpersonationSurface]` of the module (or
  the interface member it implements) is missing from core's table.
- **The core test spans the boundary with stubs.** `InMeshImpersonationReferencesTest` compiles its
  MailCopy probes against stub types declared with the same full names, because the table is core's
  subject and the real types are no longer referenceable here. The eight `MeshWeaver.Graph.Test/MailCopy*`
  suites (2 284 lines) moved with the code, into `MeshWeaver.Mail.MicrosoftGraph.Test/MailCopy`.
- **Persisted content is safe.** `MailCopySource` and `MailMessageCopy` are registered under
  `nameof(...)`, so the stored discriminator carries neither namespace nor assembly.

🚨 **No window between the halves, by order.** A method group handed to a platform callback is the
shape only the compile-time table sees: the runtime guard then sees the trusted callback, not the
in-mesh caller. So the module must never ship the moved System entry points while the table names
only the old namespace. Three ordered steps close that window:

1. Core registers the module's names beside its own (#7313, additive).
2. The Plugins move requires a platform carrying step 1 (`Requires-platform:`). Its
   `MailCopySurfaceRegistrationTest` requires the new names, with no fallback.
3. This deletion drops the old keys together with the types.

🚨 **Removing the folder is a BINARY break for any bundle that still binds it — and the order is what
keeps it linkable.** It does not move the bake key: that is the compatibility key `c<major>e<epoch>`,
and the content-surface hash is provenance only ([Platform Compatibility Ladder](../PlatformCompatibilityLadder)).
But a published Mail or Google bundle built before the move references `MeshWeaver.Graph.MailCopy.*`
in `MeshWeaver.Graph`, and on a platform without those types it is `Unlinkable`: the boot and landing
probe refuses that generation rather than letting it hit a `TypeLoadException`
([The Module Platform Link Gate](../ModulePlatformLinkGate)). So the halves land in this order, and
no epoch is declared:

1. The Plugins half merges and its `Mail` and `Google` bundles PUBLISH. They bind no core MailCopy
   type, and they run on a platform with or without the folder.
2. The fleet adopts them (a new build is adopted eagerly).
3. Only then does core's half merge.

A portal that still holds a pre-move bundle when the new core rolls has that generation refused, and
it picks up the published one; it does not crash. When this was written the pull-request ladder
measured only four module bundles and neither mail module was among them; since MeshWeaver#7409 it
links every module of the sealed `plugins` publication, mail included. The ORDER is still the guard
for this break, and the pair gate (`Pairs-with:`) enforces the first step.

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
not cover a lane in MeshWeaver.Plugins. The `memex` CLI followed the Aspire integration there before
its first nuget.org publish, and both now publish from ONE lane, MeshWeaver.Plugins'
`publish-packages.yml` (which replaced its `publish-aspire-package.yml`); core has no publish lane
left. The package owner points the nuget.org policy at that file, with `vars.NUGET_TRUSTED_USER` set
there; until then that lane's `preflight` is red by name.

## The `clients/` tree

Each client goes where its consumers are, like the deployment material above.

| Piece | Went to | Why there |
|---|---|---|
| `clients/voice-gateway` | MeshWeaver.Plugins `clients/voice-gateway` | A standalone Python container that talks to a memex over HTTP and MCP. Nothing in core builds or reads it, and core ran no job for it. It now sits beside the other clients and the Speech module, with its own `Voice gateway` workflow running its 44 pytest cases. |
| `clients/python` (the `meshweaver` SDK) | **stays** | `deploy/python-gate` and `deploy/pandas-gate`, both core-built images, `COPY` it, and it generates from core's canonical `mesh.proto`. Moving it would make a core image depend on another repository. `clients.yml` stays with it. |
| `clients/README.md` | stays, rewritten | It describes what is left and where the rest went. |

Nothing here belongs in MeshWeaver.Deploy: neither piece puts a sealed set anywhere or watches where it
runs.

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
| `deploy/{ocr,ollama,whisper,s1-extract}` and `.github/workflows/{ocr,ollama,whisper,s1-extract}-image.yml` | deploy (service images an instance MAY run beside its portal) | **moved** to MeshWeaver.Deploy (same paths) — see "The service images" below |
| `deploy/aca`, `deploy/compose`, `deploy/compose-ha`, `deploy/marketplace`, `deploy/.env.example`, `deploy/aks/{README,SELF-UPDATE,INSTANCE-LIFECYCLE}.md` | deploy (generated targets, the env template, the AKS bring-up docs) | **moved** to MeshWeaver.Deploy (same paths) |
| `control-always-latest.yml`, `prod-synthetic-probe.yml`, `combo-verify.yml` | operations | **owed**: no move opened yet |
| `deploy/base-images` + `base-image-acr.yml` | build (the portal image's own base) | stays: `portal-ai` is the base `main-cd.yml` builds the product image FROM, and `dotnet-test.yml` holds CI's fonts and Playwright version to its Dockerfile |
| `deploy/core-tests` | build (the set's fifth image, `mw-core-tests`) | stays: `main-cd.yml` packs and pushes it, `dotnet-test.yml` self-tests `core-tests.py`, `.github/build-stages.json` runs it |
| `deploy/{node,python,pandas}-gate` | build recipes over core source | stay: each is built from the REPOSITORY ROOT and packs core's own SDK (`clients/python`, `clients/typescript`) and the one canonical `mesh.proto` — moving them would make another repository check core out at a ref to build core's bytes. No workflow builds them; they are core's language-gate reference |
| `deploy/CHANGE-PROPAGATION.md` | the platform's own delivery cost model | stays: it measures core's CI/CD and the bake tax, and `Directory.Build.props` cites it as the cost/benefit record of the framework-identity stamp. It belongs in the doc tree rather than `deploy/` — a follow-up, not a move |
| `edge-images.yml`, `dockerhub-mirror.yml` | build (images the product or its CI runs) | stays |
| `build-follow.yml`, `build-host-publication.yml`, `build-follow.py`, `build-executor.py`, `build-artifacts-fetch.py`, `compose-gate-host.sh`, `sandbox-postgres.sh`, `module-build-key.py`, `node-repo-scope.py`, `ci-artifact-store.py` | build (the CI side of a host run; helpers the runner image READS at the set's commit) | stays |
| `src/MeshWeaver.Deployment.Contract` (`BuildHostsBlock`, `BuildHostSetKeys`, `BuildHostRetention`, `CoreTestsDigest`) | contract | stays: the receivers in MeshWeaver.Plugins and the records compile against it |

🚨 **The runner image is two repositories' bytes, like the operator's.** The runner is
MeshWeaver.Deploy's; its helpers and `meshweaver-plugin-build` are core's, fetched BY COMMIT inside
the image build (`PLATFORM_COMMIT` = the set's own commit), never vendored, so the build context is
empty. So a core change to a helper reaches the next set's runner, and a Deploy change to the runner reaches
the next set too: `mw-build-host:<version>` is written ONCE per version, because the hosts are
handed it by digest.

### The service images — the sidecar move

The four service images an instance may run BESIDE its portal — Tesseract OCR (`ocr`), the Ollama
model service (`ollama`), Swiss-German whisper (`whisper-swiss-german`) and GLiNER extraction
(`s1-extract`) — are neither the product image set nor anything core's CI runs. Each is a
self-contained build context (its own directory, no core source), and each one's consumer is a
deployment: the estate vendors the chart, an approved `ApplyChart` binds the immutable tag. So by
the test above they are **deploy**, and they left with their lanes, their charts and their tests. A
service image built only for the platform's own tests would have stayed.

What made it more than a copy:

1. **The images keep their names.** Same registry repository, same line tag; every tag already in
   the registry stays valid. Only the per-build `<line>-<sha7>` changes meaning: since the move the
   seven characters name a MeshWeaver.Deploy commit. Retention is unchanged — the line tags stay in
   core's `.github/release-channels.json`, their writers declared with `"repo":
   "Systemorph/MeshWeaver.Deploy"` exactly as the hosting operator's is with MeshWeaver.Plugins, so
   `release-channels.py check` does not look for the workflows here and the pruners still protect
   the lines.
2. **The publishing identity is narrower.** Core's lanes signed in with a subscription and reached
   the registry through `az acr` and `acr-login.sh` (both an ARM lookup). MeshWeaver.Deploy's ONE
   cloud credential holds AcrPush on the registry resource and nothing else, federated to its `main`,
   so the moved lanes sign in tenant-level and talk to the registry through its data plane
   (`acr-data-plane.py`: `login`, and a byte-for-byte `retag` for the ollama line, which must not
   re-upload ~25 GB). Its `service-images-guard.yml` holds every lane to that on each pull request.
3. **Core's last overlay files left with the charts.** The four `deploy/*/helm/values.yaml` were the
   only files in this tree that match the retention lane's overlay path filter, so this repository's
   entry in `.github/acr-retention/instances.json` `repositories` went with them — the nightly
   `--discover` run reds a declaration nothing answers to. `lock-pinned-digests.py --self-test`
   ARM 35 now pins that this repository is declared exactly when its own tree carries an overlay
   file, in both directions.
4. **The estate checks its vendored copies against the new home.** Its `Vendored charts match core`
   job compared its copies of the whisper, s1-extract and ollama charts with core's `main`; it is
   repointed at MeshWeaver.Deploy (same paths) before the core copies are deleted.
5. **Frozen, not just deleted.** `scripts/check-chart-freeze.py` freezes every moved path
   delete-only, so a second publisher of those image names cannot grow back beside Deploy's.

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

A second pass, after the Razor extension, the module-libraries shelf, `MeshModulesPublish.targets` and
`Memex.Portal.ServiceDefaults` had left, found five more: the Roslynator analyzers. They appeared in
no graph because the only reference was a `PackageReference Update=` block in
`MeshWeaver.Layout.csproj`, and an `Update` with no matching `Include` adds nothing. Both were
deleted. Every satellite that runs Roslynator pins it in its own list.

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
