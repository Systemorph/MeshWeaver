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

🚨 **Always pass both survivors to the sweep's `keep` input.** The subtraction alone protects only
what THIS tree packs, and one survivor is in another repository — invisible to the derivation by
construction. That gap has already cost one wrong unlist: an earlier sweep run from this repository
could not see the template's project and retired it.

### 🛑 The template is a survivor that cannot ship yet

`MeshWeaver.MemexTemplate` last published `3.0.0-rc7`; all seven of its versions read
`listed: false`, so a versionless `dotnet new install MeshWeaver.MemexTemplate` cannot resolve it.
One cause was mechanical and is fixed — its pack target never passed the generator the platform
checkout it requires, so `dotnet pack` on it could not succeed at all. The other is **a product
decision that is not made**, and it blocks publication. Measured 2026-09-07:

| Fact | Consequence |
|---|---|
| `generate-memex-template.cs` **refuses** to generate without `--with-gui` — the two shipped hosts reference `Memex.Portal.Gui` unconditionally | there is no GUI-less template to publish |
| `Memex.Portal.Gui` lives **only** in MeshWeaver.Plugins, which is **private**. Core holds zero `.razor` files and no `MeshWeaver.Blazor*` project | publishing the template publishes private source |
| The published `3.0.0-rc7` package contains **no** `Memex.Portal.Gui` | shipping it now is a NEW exposure, not a restoration |
| A nupkg cannot be recalled — unlisting hides a version from search but it stays downloadable by exact version for ever | the exposure is irreversible |

There *is* a standing decision to include the GUI (2026-08-26), but its stated premise — *"the UI
is public in core today, the move is what would make it private"* — **expired when the move
happened.** So `publish-packages.yml` publishes the Aspire integration only, and the template's
step is **absent rather than written-and-disabled**: a step that exists but never runs is the
skip-trapdoor this repository forbids, and one that runs would ship the source. Tracked in
[#3653](https://github.com/Systemorph/MeshWeaver/issues/3653).

Either answer unblocks it — publish the GUI source deliberately, or change the hosts so a GUI-less
template builds. Neither is a packaging decision.

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
- **The template needs both trees, and neither alone is enough.** Its generator copies six
  projects — four from MeshWeaver.Plugins, two from MeshWeaver — plus three assets only core has
  (`samples/Graph/Data/User`, `samples/Graph/Data/ACME`, and the `Directory.Packages.props` the
  generated template's pins are read from). So it takes `<portalRoot> --core <meshweaverRoot>` and
  can run in neither repository alone. `projectsToCopy` in `generate-memex-template.cs` is the
  authority on the split; it moves as projects move, so read it rather than a remembered ratio.
  The lane checks out MeshWeaver.Plugins with a **minted GitHub App token** — never a stored PAT.
  Its predecessor, `publish-github.yml`, carried `secrets.GH_PAT` and is deleted.
- **The publish is verified.** `dotnet nuget push` exiting 0 reports that the call was made, not the
  resulting state; the lane polls nuget.org until both versions read `listed: true`.

> 🚨 **A core tag does not name a MeshWeaver.Plugins commit,** and no pairing artefact exists to
> resolve one. The lane takes the plugins default branch and **records the resolved sha** in the job
> summary so the pairing is observable after the fact. When a pairing artefact appears, read it there
> instead.

---

## 5. What was retired

Forty-three package ids. Thirty-nine stopped at `3.0.0-rc13`, three at `rc8`
(`MeshWeaver.Fixture`, `MeshWeaver.Hosting.Monolith.TestBase`,
`MeshWeaver.Hosting.Orleans.TestBase`) and one at `rc7`
(`MeshWeaver.Markdown.Collaboration`) — nothing has been published since the rc line closed.

> 🚨 **`v3-flatcontainer/index.json` sorts versions as STRINGS, not as versions.** `rc10`…`rc13`
> therefore sort BEFORE `rc9`, so reading the tail of that list reports `rc9` as the newest and is
> wrong by four releases. It fooled this page's first draft. Order with a SemVer comparison, or
> read the registration blob, whenever "the latest version" matters. The largest by downloads were `MeshWeaver.ShortGuid`, `MeshWeaver.Utils`,
`MeshWeaver.Domain`, `MeshWeaver.Reflection`, `MeshWeaver.ServiceProvider`,
`MeshWeaver.Messaging.Contract` and `MeshWeaver.Messaging.Hub`; the set also included the three
dotnet tools (`MeshWeaver.Cli`, `MeshWeaver.Compiler.Cli`, `MeshWeaver.ThumbnailGenerator`) and
`MeshWeaver.Reactive.Assertions`, whose only out-of-repo consumer references it by **project**
against `$(MeshWeaverRoot)`.

### ✅ Applied and verified (2026-09-08) — and why it took several runs

**The retirement is complete.** Apply run
[34173649521](https://github.com/Systemorph/MeshWeaver/actions/runs/34173649521) (2026-09-08
00:32Z) skipped 29 versions that were already unlisted, unlisted the rest, and ended with
`VERIFIED on attempt 4: every version now reads as unlisted` — the verification reads the
registration blobs, not search. Re-dispatching the same lane with `apply` is the standing
re-verification: it skips everything already unlisted and re-verifies, so it is safe to run at any
time and costs almost no quota. What follows is the record of why one pass was never enough.

**The retirement was applied in batches, not in one pass.** Measured on the first apply run
(2026-09-07, 760 versions across the 43 ids):

| Elapsed | Deletes accepted | Refused `403 (Quota Exceeded)` |
|---|---|---|
| minute 1 | 98 | 0 |
| minute 2 | 141 | 0 |
| minute 3 | 70 | 73 |
| minute 4 | 0 | 158 |
| minute 5 | 0 | 155 |
| minute 6 | 31 | 12 |
| minute 8 | 22 | 0 |

So roughly **300 deletes land, then the endpoint blocks for ~2.5 minutes, then the budget
refills** — and the server names the wait itself (`retry after: 145-160s`). That first run
unlisted **362 versions, verified**, and spent 398 round trips rediscovering the same 403.

Three consequences are built into `orphaned-nuget-packages.py` rather than remembered:

1. **Already-unlisted versions are skipped before any delete is called.** Deleting an unlisted
   version is a no-op that still costs a quota slot, so without this a resumed run spends its
   entire budget redoing finished work. This is what makes the job completable at all.
2. **The server's retry hint is honoured, once, then the run stops.** Waiting the time nuget.org
   names is using the API correctly; hammering keeps the quota pinned and teaches the log nothing.
3. **A quota wall exits `2` — RESUMABLE — and names exactly what remains.** It is neither a
   success nor a failure, and the workflow summary says so in those words. 🚨 Re-running the same
   dispatch is the whole recovery procedure; there is nothing to edit between runs.

> 🚨 **Read the end state from the registration blobs, never from search.** nuget.org's search
> index lags the blobs by a wide margin — mid-retirement it reported 25 listed `MeshWeaver.*` ids
> while the per-version `listed` flags told a different and more advanced story. `is_listed()` in
> the script reads the blob, which is the same source the verification step trusts.

> 🚨 **`Memex.Merlin.*` belongs to a different company.** It surfaces in a nuget.org search for
> "MeshWeaver" because the search is full-text. The sweep only ever considers ids that are exactly
> `MeshWeaver` or begin `MeshWeaver.`, and that filter is the reason — do not relax it.

---

## 6. Ownership — what still has to move to Systemorph

Unlisting does not touch ownership, and nuget.org exposes **no API for owner management** — every
step below is a web action by the current owner, so this section is the checklist. Measured
2026-09-08 09:4xZ against the nuget.org search service and profiles:

| Fact | Consequence |
|---|---|
| `MeshWeaver.Aspire.Hosting.Memex` — owners: **`rbuergi`** only, 12 listed versions | the surviving package is personally owned |
| `MeshWeaver.MemexTemplate` and every retired id — fully unlisted, ownership unchanged | still `rbuergi`'s; an owner can re-list any version |
| `verified: false` on the survivor | the **`MeshWeaver.*` ID prefix is not reserved** — anyone can publish a new `MeshWeaver.X` |
| nuget.org profile `Systemorph` exists | the organisation account to transfer to is there |
| `publish-packages.yml` pushes with `secrets.NUGET_PAT` | the key is the owner's, not the organisation's |

The transfer, in the order that keeps publishing working throughout:

1. **Add the organisation as owner** of each id — survivors first, then the retired ids, so nothing
   under the prefix stays personally owned (a retired version stays downloadable by exact version
   for ever, and re-listing is an owner's act). nuget.org → package → *Manage owners* → add
   `Systemorph`; the organisation accepts the invitation.
2. **Reserve the `MeshWeaver.*` ID prefix for `Systemorph`** (nuget.org's ID-prefix reservation
   request). Until then the `verified` badge stays off and the prefix is open.
3. **Rotate `secrets.NUGET_PAT`** to an API key minted under the `Systemorph` organisation, scoped
   to push `MeshWeaver.*`. The lane's preflight names that secret and goes red naming it if absent,
   so a missing rotation is loud, not silent.
4. **Remove `rbuergi` as owner** only after step 3 has published once from the organisation's key.

---

## 7. See also

- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — the one version number, the two
  channels, and §5 on why NuGet stopped being a delivery vehicle.
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — how plugins actually ship: bundles, not packages.
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the image as compiler and reference set.
- [Deployment](/Doc/Architecture/Deployment) — where the built images go.
