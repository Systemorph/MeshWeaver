---
Name: "NuGet Package Retirement"
Abstract: "Two packages survive — the Aspire integration and the dotnet new template. Everything else under the MeshWeaver prefix is retired and unlisted. Why, what unlisting does and does not do, where the retirement tool lives, and the ground rule that keeps the survivor count at two."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#455a64'/><path d='M12 5l5 2.75v5.5L12 16l-5-2.75v-5.5z' fill='none' stroke='white' stroke-width='1.5' stroke-linejoin='round'/><path d='M7 7.75L12 10.5l5-2.75M12 10.5V16' stroke='white' stroke-width='1.5' fill='none' stroke-linejoin='round'/><path d='M5 19h14' stroke='#ef5350' stroke-width='2' stroke-linecap='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Release"
  - "NuGet"
  - "Packaging"
---

# NuGet Package Retirement

> *"which nuget packages do we want to release at all? should we discontinue all but the aspire
> adapter? … we retire most nuget packages completely … we leave the dotnet new package and the
> aspire integration"* — maintainer, 2026-09-07

Two packages survive. Everything else ever published under the `MeshWeaver` prefix is retired and
unlisted on nuget.org.

---

## 1. The two survivors

| Package | Project | What it is |
|---|---|---|
| `MeshWeaver.Aspire.Hosting.Memex` | `memex/aspire/Memex.Aspire.Hosting` (this repository) | The Aspire hosting integration. `builder.AddMemex()` wires Postgres (pgvector), the one-shot migration and the portal from published GHCR images, and publishes to Docker Compose, Kubernetes/Helm or Azure Container Apps. |
| `MeshWeaver.MemexTemplate` | `tools/MeshWeaver.MemexTemplate.Pack` (**MeshWeaver.Plugins**) | The `dotnet new` template pack that writes a portal solution. |

Both survive for the same reason: **they are entry points, not platform bytes.** One writes the
solution a newcomer starts from; the other is the AppHost-side integration that names the images.
Neither is loaded into a portal, neither carries a framework assembly, and neither participates in
the NodeType bake identity.

Everything else was a *library*, and a MeshWeaver library package has had no consumer for months:
in-mesh source compiles against the platform **image**, module bundles carry their own closures, and
satellite repositories build inside `mw-plugin-test`
([Release Process §5](/Doc/Architecture/ReleaseProcess),
[Plugin Packaging](/Doc/Architecture/PluginPackaging),
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture)). Publishing them meant
offering consumers packages nobody builds against and nobody patches.

---

## 2. The ground rule: startup dependencies are Aspire options, never packages

> *"everything which has a startup-dependency should be configurable via aspire plugin"* —
> maintainer, 2026-09-07

This is what keeps the survivor count at two. The question it settles is the obvious objection —
*surely feature configuration needs packages? which database driver to load, which plugins to
operate it?* It does not, and one fact decides it:

**The AppHost is not the portal.** The adapter runs in the orchestration process; the portal is a
prebuilt image pulled from GHCR. A `PackageReference` a customer adds to their AppHost lands in the
*AppHost's* output, where the portal container can never see it. A NuGet package therefore cannot
add a driver to a running portal even in principle — so a fan-out of `MeshWeaver.Hosting.<driver>`
packages would buy nothing and cost a version matrix that must agree with the image tag.

What does work is already in place. `MemexOptions` is the single config surface, and every value on
it maps 1:1 to a portal config key emitted as container environment:

```csharp
builder.AddMemex("memex", o => o
    .WithBackend("Filesystem")            // → Backend
    .WithOrleansClustering("AdoNet")      // → Orleans__Clustering
    .WithEmbeddings(endpoint, key)        // → Embedding__Endpoint / __ApiKey
    .WithAiProviders(openAI: false));     // → Features__Ai__Providers__OpenAI
```

Driver selection is already done this way — `Backend` picks Filesystem or Azure blob,
`OrleansClustering` picks Localhost, AdoNet or Azure Tables — as **config strings the image
interprets at startup**, not as assemblies the customer resolves. Selecting which plugins the image
loads at startup is the same shape: a value on `MemexOptions`, a config key on the container, and
the plugin fetched at runtime from the plugin catalog as a module bundle. Adding a startup-time
capability means **adding an option to the adapter**, never adding a package.

> 🚧 **Open direction, not yet built —
> [#3646](https://github.com/Systemorph/MeshWeaver/issues/3646).** Two consequences of this ground
> rule are recorded so they are not rediscovered: the adapter should grow explicit plugin selection
> (today plugins are configured portal-side, not from the AppHost), and the hand-maintained Helm
> chart under `deploy/helm/` duplicates keys that `MemexOptions` already owns — it should be
> **generated** from the same surface rather than kept in parallel. Aspire's own Kubernetes/Helm
> publisher is the mechanism.

---

## 3. What unlisting does — and what it does not

`dotnet nuget delete` **unlists** on nuget.org; it does not erase. This is what makes the retirement
safe to automate at all:

| | |
|---|---|
| ✅ Existing pins keep resolving | A `PackageReference` at an **exact** version still restores. |
| ✅ Reversible | A listing can be restored from the nuget.org UI, and publishing a new version relists the id. |
| ❌ Gone from search | The package no longer appears in nuget.org search or `dotnet add package` completion. |
| ❌ Gone from latest-version resolution | A floating range or a versionless `dotnet tool install -g` no longer finds it. |

**This is measured, not assumed**, three ways:

1. **A direct restore.** `MeshWeaver.AI 2.5.0` reads `listed: false` on nuget.org. A project
   referencing exactly that version, against nuget.org as the only source, restores in about three
   seconds. That is the property the whole retirement rests on, verified against the live registry
   rather than inferred from NuGet's documentation.
2. **An earlier sweep already did this.** `MeshWeaver.AI`, `MeshWeaver.Blazor`,
   `MeshWeaver.Hosting.Blazor`, `MeshWeaver.Import` and `MeshWeaver.Charting` are already unlisted.
   `Systemorph/Memex` pins about thirty-five of them at `3.0.0-rc2`, restores from nuget.org and
   nothing else, and its `Build (Release)` has stayed green throughout — for weeks.
3. **Every downstream consumer pins exactly.** Six repositories in the organisation reference
   `MeshWeaver.*` by `PackageReference`, and every one of them fixes the version through central
   package management — nothing floats:

| Repository | Pinned at |
|---|---|
| `Systemorph/Memex` | `3.0.0-rc2` |
| `Systemorph/ILS` | `3.0.0-preview1` |
| `Systemorph/CreditReRate` | `2.5.0` |
| `Systemorph/PartnerRe.Aviation` | `2.4.0` |
| `Systemorph/PartnerRe.PropertyFac` | `2.4.0` |
| `Systemorph/Solar` | `1.0.0` / `1.0.1` |

> 🚨 **A floating range is what unlisting would break** — `Version="2.*"`, a versionless
> `dotnet tool install -g`, or a `PackageReference` with no version at all under a feed that
> supplies one. Before retiring any further id, check for those rather than for the *presence* of a
> reference: a reference is not the risk, an unpinned one is. The check that matters is an
> organisation-wide code search for `PackageReference Include="MeshWeaver`, followed by reading each
> hit's `Directory.Packages.props`.

The two consumption paths that unlisting *does* end are both deliberate:

- `dotnet tool install -g MeshWeaver.Cli` (the `memex` CLI) and `-g MeshWeaver.Compiler.Cli`
  (`mw-compiler`). No lane in this repository or any satellite installed either: CI reaches those
  verbs through the `mw-plugin-test` **image**, and `MeshWeaver.Plugin.Build` is built from source
  at the pinned platform ref on purpose, because it encodes how a given version compiles.
- Anything resolving `MeshWeaver.*` by a floating version. Nothing in the fleet does.

---

## 4. How the retirement is enforced

Retirement is **derived**, never a hand-kept list — so it stays correct as further modules move out.

**Half one — nothing packs unless it says so.** `Directory.Build.props` sets
`<IsPackable>false</IsPackable>` at the repository root, and exactly one project opts back in. That
is a default rather than a ban: it exists so that adding a project cannot silently add a package.

**Half two — the sweep.** `scripts/orphaned-nuget-packages.py` computes

```
orphaned  =  (published under the MeshWeaver prefix)  MINUS  (what this tree packs)  MINUS  (--keep)
```

and `.github/workflows/unlist-orphaned-packages.yml` runs it. It is **report-only** unless `apply`
is checked *and* `confirm` reads `UNLIST` — because the derivation is a subtraction, so any bug in
"what this tree packs" fails toward unlisting live packages, and report-only is what caught such a
bug once computing "all 90".

> 🚨 **`IsPackable` is inherited, and the sweep reads csproj TEXT.** The script does not evaluate
> MSBuild, so a repository-level default is invisible to it unless it is taught the props chain —
> and it is: `directory_default()` resolves the chain the way MSBuild does (an explicit declaration
> wins; a props file that neither declares nor imports the one above ends the chain at MSBuild's own
> default, `true`). Getting this wrong in the *other* direction is the dangerous one — believing a
> project does not pack is what unlists a live package — so the resolution deliberately errs toward
> packable, and four self-tests pin both directions.

**Half three — the set is asserted.** `PackableProjectReadmeTest` fails if the effectively-packable
set is anything other than the one expected project. A project that quietly regains packability
would otherwise do two invisible things at once: enter the release's pack glob, and *spare* its
retired package from the sweep.

### Publishing the survivors

`.github/workflows/publish-packages.yml` packs and pushes both, on the release tag `v*.*.*`.

- **Why the tag and not every merge.** nuget.org orders by SemVer, and the continuous `-ci.<n>`
  stream does not sort the way the fleet's self-updater ranks it. A continuous stream on nuget.org
  would reproduce the rc line's defect exactly — `rc9` sat above `rc13` for a whole run — in front
  of consumers whose only question is "what is the current one".
- **Why pack rather than promote.** A release is a *promotion* of images because those bytes already
  exist in a registry; no nupkg is ever built by `main-cd`, so there is nothing to retag. Packing at
  the tag is safe **only** because neither survivor is a platform assembly. If either grows a
  dependency on a MeshWeaver framework assembly, this lane is wrong.
- **The template needs both trees.** Its generator copies six projects split 3/3 across MeshWeaver
  and MeshWeaver.Plugins, so it takes `<portalRoot> --core <meshweaverRoot>` and can run in neither
  repository alone. The lane checks out MeshWeaver.Plugins with a **minted GitHub App token** — never
  a stored PAT. Its predecessor, `publish-github.yml`, carried `secrets.GH_PAT` and is deleted.
- **The publish is verified.** `dotnet nuget push` exiting 0 reports that the call was made, not the
  resulting state; the lane polls nuget.org until both versions read `listed: true`.

> 🚨 **A core tag does not name a MeshWeaver.Plugins commit,** and no pairing artefact exists to
> resolve one. The lane takes the plugins default branch and **records the resolved sha** in the job
> summary so the pairing is observable after the fact. When a pairing artefact appears, read it there
> instead.

---

## 5. What was retired

Forty-three package ids, every one stalled at `3.0.0-rc7`/`rc8`/`rc9` — nothing has been published
since the rc line closed. The largest by downloads were `MeshWeaver.ShortGuid`, `MeshWeaver.Utils`,
`MeshWeaver.Domain`, `MeshWeaver.Reflection`, `MeshWeaver.ServiceProvider`,
`MeshWeaver.Messaging.Contract` and `MeshWeaver.Messaging.Hub`; the set also included the three
dotnet tools (`MeshWeaver.Cli`, `MeshWeaver.Compiler.Cli`, `MeshWeaver.ThumbnailGenerator`) and
`MeshWeaver.Reactive.Assertions`, whose only out-of-repo consumer references it by **project**
against `$(MeshWeaverRoot)`.

> 🚨 **`Memex.Merlin.*` belongs to a different company.** It surfaces in a nuget.org search for
> "MeshWeaver" because the search is full-text. The sweep only ever considers ids that are exactly
> `MeshWeaver` or begin `MeshWeaver.`, and that filter is the reason — do not relax it.

---

## 6. See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — the one version number, the two
  channels, and §5 on why NuGet stopped being a delivery vehicle.
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — how plugins actually ship: bundles, not packages.
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the image as compiler and reference set.
- [Deployment](/Doc/Architecture/Deployment) — where the built images go.
