---
Name: Docs Follow The Functionality
Category: Architecture
Description: Where a documentation page belongs — with the code it documents, exactly as a skill lives in the module it is about. The measured destination map for the 996 files under Data/, the delivery mechanism that would carry a module's docs, and the counter-list of what stays in core and why.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/><path d="M12 7v6"/><path d="m9 10 3 3 3-3"/></svg>
---

# Docs Follow The Functionality

**A documentation page belongs in the module it documents.** MCP docs with the MCP module, AI docs
with the AI engine, GUI docs with the GUI. Core's own mechanisms — the compiler, the mesh, node-type
compilation, module versioning — keep their docs in core, *because core is the functionality they
document.*

This is not a new rule. It is the rule already written down for skills, applied to the other kind of
prose the mesh serves:

> 🚨 **Skills are DECENTRAL — they live in the module they are about** … skills are injected by the
> **partition of the NodeType** and of the **data element in view** — placement IS the targeting.

Documentation has the same shape. A doc page is a `Markdown` node in a partition, found by
`search namespace:Doc scope:descendants <topic>` and by the vector index. Put it in the module's
partition and it is found by anyone working in that module, for the same reason a skill is.

**Measured against `Systemorph/MeshWeaver@f8e208567` and `Systemorph/MeshWeaver.Plugins@7e0750c2`
(2026-09-01). Re-measure before acting; the numbers move daily.**

## The starting position, corrected

`src/MeshWeaver.Documentation/Data/` holds **996 files**, not 985 — the tree grew by 11 while the
survey ran, which is itself the point: this is a live stream, not an archive.

| Folder | Files | `.md` | `.json` | `.cs` | Executable blocks | Inbound `/Doc/` links |
|---|---|---|---|---|---|---|
| `WhatsNew` | 709 | 709 | | | 0 | 7 |
| `Architecture` | 170 | 163 | 3 | 4 | 17 (in 7 pages) | 838 |
| `DataMesh` | 53 | 32 | 14 | 7 | 27 (in 17 pages) | 77 |
| `GUI` | 42 | 42 | | | 84 (in 26 pages) | 113 |
| `AI` | 13 | 13 | | | 1 (in 1 page) | 63 |
| root pages | 6 | 6 | | | 3 | 2 |
| `PreReleaseNotes` / `ReleaseNotes` | 3 | 3 | | | 0 | 0 |

The **executable-block** and **inbound-link** columns are the cost columns, and they do not track
each other. They are what makes one folder nearly free to move and another prohibitive.

The repo has **91 `.csproj`** (36 `src/`, 44 `test/`, 5 `tools/`, 3 `memex/`, 2 `samples/`, 1
`deploy/`) — the "79 under `src/`" in the original framing was the `src/`+`test/` total, and only
`src/` counts as the platform surface.

## 🚨 First: `Data/` is the SOURCE, not a second copy

This had to be settled before anything else, because if the mesh held an independently-authored copy
then drift would outrank placement. It does not.

The tree is compiled into `MeshWeaver.Documentation.dll` as embedded resources and reaches a mesh one
of two ways — the in-memory `EmbeddedResourceStorageAdapter` partition, or, when `Doc` is
DB-synced, `DocumentationStaticRepoSource` materialising the same embedded resources into Postgres
on boot (see [Static Repo Import](../StaticRepoImport)). Both read the same bytes. And the partition
is authored read-only: `Doc/_Policy` sets `Create/Update/Delete = false`, so a live edit is not
merely discouraged, it is refused.

Measured against `memex.meshweaver.cloud` (running `0a1eabdc0`, 12 commits behind `main`):

| | Disk | Mesh | On mesh but not on disk |
|---|---|---|---|
| `Doc/WhatsNew` | 709 | 702 | **0** |
| `Doc/Architecture` (top level) | 161 | 159 | **0** |

Every difference is disk-ahead — the 7 WhatsNew entries and 2 Architecture pages dated after the
deployed image was built. **Zero mesh-only nodes in either subtree.** The mesh copy is derived and
lagging, never divergent.

🚨 **The finding that matters is the lag, not the copy.** A doc fix reaches a mesh only through a
**core image roll**, because the bytes ride inside `MeshWeaver.Documentation.dll` and that dll ships
in the portal image. That is a strictly slower channel than the one every module already uses, and it
is an argument *for* module-shipped docs rather than against them.

## The destination map

### `AI` (13 files) → the `AI` package. The clearest move in the tree.

Every symbol these pages name resolves in `MeshWeaver.AI` and its provider modules, and **not one of
them resolves in core**: `ChatCommands` 11-0, `ProviderConfiguration` 11-0, `ModelTiers` 2-0,
`AgentDesign` 2-0, `ModelProviderSetup` 12-4, `ModelProviderSettings` 14-6. `TeamsBot` belongs with
`Teams`, `ExecutiveAssistant` with `Mail`, `McpAuthentication` with `Mcp`.

The AI engine [left the platform repo](../CarvingProjectsOutOfCore) with its built-in agent and skill
roster. Its documentation did not travel with it. That is the same half-finished-move shape the
carving survey names — and the cost of finishing it here is the lowest in the tree: **1 executable
block across all 13 pages**, so almost nothing to re-gate.

### `WhatsNew` (709 files) → **stays, and the mechanism is already decentral.**

This one looked like the big prize and is not. `Doc/WhatsNew` is deliberately **one fleet-wide feed**,
and a satellite repo already files into it without a cross-repo PR: drop
`WhatsNew/<date>-<slug>.md` with `nodeType: WhatsNew` in its own tree. The portal's What's New tab
runs **two** queries — `path:Doc/WhatsNew scope:children` for the platform's own entries and a
node-type query for everyone else's — and de-duplicates by path. `WhatsNew/` is a reserved
non-package directory in eight scripts across both repos.

Measured: of the 709 entries, **60 (8.5 %)** name a package that lives outside core and **105
(14.8 %)** mention plugins at all. Those are history. New ones already go to the right place.

🚨 **What the survey found instead is that the decentral lane is UNGATED.** Core's 709 entries are
pinned by `WhatsNewEntryIntegrityTest` (ISO-dated filename; `Name`, `Category ∈ {Feature, Fix}`,
`Description`, `Order = -yyyyMMdd`). Nothing runs that check over a satellite's `WhatsNew/`. Three of
the four entries in the plugins repo today carry `title:`/`category:`/`date:` and **no `Description`
and no `Order`** — the exact malformed shape the core test was written to catch, in the feed, right
now, invisible. Rendering degrades quietly: no `Order` means the day sorts alphabetically by title.

**The fix is not a move.** It is to run the same pin on the satellite lane — the ratchet-in-every-repo
pattern of [the CI policy](../ReadingCiSignals) — so a note authored anywhere is judged everywhere.

### `GUI` (42 files) → **stays in core.**

This is the counter-result the axis produces, and it is worth stating plainly because "the Blazor
renderers live in plugins" makes the opposite look obvious. It is wrong. A GUI doc documents the
**control**, and controls are declared in `MeshWeaver.Layout`, which is core. Of 42 pages, 30 name
**zero** plugins-resident symbols; the remainder are dominated by core symbols
([Data Binding](/Doc/GUI/DataBinding): 19 core to 3). The renderer is an implementation of the
control contract, not its subject.

The cost side agrees, hard: `GUI` carries **84 of the tree's 132 executable blocks, in 26 of its 42
pages.** Those blocks are compiled *and executed* against a real kernel by `DocExecutableBlocksTest`
and `DocumentationCodeBlockCompilationTest`, and browser-rendered by `DocExamplesRenderTest`. A
`GUI` page is not prose; it is gated content. Moving it means moving its gate.

Two genuine exceptions: `React/*` (7 pages) belongs with the React client that already moved, and
`BlazorCustomControls` with `MeshWeaver.Blazor.Radzen`.

### `DataMesh` (53 files) → **stays in core**, with two departures.

Symbol residence is overwhelmingly `MeshWeaver.Graph`, `MeshWeaver.Mesh.Contract` and
`MeshWeaver.Data` — nodes, node types, the unified path, query syntax, CRUD. This is the platform's
own data model. 27 executable blocks in 17 pages.

The departures: `CollaborativeEditing` (plus its 6 `_Comment` fixtures) documents
`MeshWeaver.Markdown.Collaboration`, delivered as the `Essentials` package; and `SocialMedia/` — a
page plus **two NodeTypes with 5 `Source/*.cs`** — is a worked example whose subject has a whole
satellite repo. Note carefully what `SocialMedia` *teaches*: how to author a NodeType. That is a core
mechanism wearing a domain costume, so the honest split is to keep the lesson and re-cast the example,
not to relocate the page.

### `Architecture` (170 files) → **splits, and does not split by folder.**

The largest and least homogeneous group. Classified by resolving each page's backticked symbols
against both repos' declaration index, then hand-checking:

**Move — the subject lives in a plugins module:**

| Page | Destination | Signal (plugins-only / core-only symbols) |
|---|---|---|
| `ModelProviders`, `AgenticAI`, `AgentFrameworkStores`, `ThreadOperations`, `ThreadExecutionStreaming` | `AI` | 8-0, 2-0, 9-1, 16-7, 4-3 |
| `PixelFaithfulExport` | `Export` | 15-4 |
| `LogWatchTriage` | `Observability` | 9-6 |
| `CentralizedSpeech`, `OnDeviceVoice` | `Voice` | 4-2, weak |
| `Notifications`, `EmailIngestionAndNotifications` | `Notifications`, `Mail` | 1-4, 8-9 |
| `InstanceSync`, `CrossInstanceMirror` | `InstanceSync` | 3-3, weak |
| `DeploymentAKS`, `ReleaseGates`, `Instances`, `InstanceIdentityAndSetup` | `Hosting` | 2-4, 1-8, weak |
| `LinkPreviews` | `OgCard` | weak — see the caveat below |

**Stay — core is the functionality:** `NodeTypeCompilation` (43 core symbols to 4),
[Plugin Packaging](../PluginPackaging) (23-2), [Plugin Registry](../PluginRegistry) (19-1),
[Plugin Build Contract](../PluginBuildContract) (7-0), [Module Versioning](../ModuleVersioning) (2-0),
[Module Build Architecture](../ModuleBuildArchitecture) (1-0), and the whole reactive/storage/security
spine. The plugin *system* is core — `MeshWeaver.PluginCatalog` is 21 245 lines of it — so the pages
that document it stay, even though every plugin they describe lives elsewhere. **Documenting a
consumer is not owning it.**

🚨 **The symbol signal is a lead, never a verdict.** It reads backticked identifiers, so a
narrative page with few code names (`OnDeviceVoice`, `LinkPreviews`, `MenuAsData`) scores near zero
in both columns and must be judged by subject. It also over-counts `MeshWeaver.AI`, which declares
enough types to appear in pages that have nothing to do with it. And it is blind in exactly the way
[the carving survey](../CarvingProjectsOutOfCore) records: *"a reference may be dead weight, and a
live consumer may have no reference at all."*

## The mechanism: `MeshDocs/`, and why it is easier than skills were

`MeshWeaver.Plugins#1060` establishes the pattern for skills: a module embeds
`MeshSkills/{Package}/{id}.md`, an `IStaticNodeProvider` reads the resource **names** of the
assemblies the mesh installed, and the node lands at `{Package}/Skill/{id}`. One csproj line, no
reference to the AI engine, and — the whole point — the skill travels inside the artifact it belongs
to, so it cannot be delivered separately and go missing.

**The same shape carries documentation, and every ingredient is already core:**

| Ingredient | Home |
|---|---|
| `InstalledModuleAssembly` | `MeshWeaver.Mesh.Contract` |
| `IStaticNodeProvider` | `MeshWeaver.Mesh.Services` |
| `MarkdownFileParser` / `FileFormatParserRegistry` | `MeshWeaver.Hosting.Persistence.Parsers` |
| the `Markdown` node type | core |

So a `ModuleDocProvider` reading `MeshDocs/{Package}/…` into `{Package}/Doc/…` belongs in
`MeshWeaver.Hosting` and needs **no AI dependency at all** — strictly simpler than the skill case,
which had to live in `MeshWeaver.AI` because `SkillMarkdown` and `SkillNodeType` do.

Four things are genuinely different and must be designed, not copied:

1. **Depth.** A skill id is one segment; `ModuleSkillProvider` rejects anything else outright
   (`parts.Length != 2`). Docs nest three deep. The split becomes a path walk — and every folder
   level needs a backing node file, the way `Architecture.md` backs `Architecture/`, or the folder is
   nothing at all in the mesh.
2. **Page assets.** A doc page's inline embeds resolve through a per-node embedded content collection
   that `AddDocumentation` wires by testing for the `Doc/` prefix, plus the public
   `/static/DocContent` build-asset mount. Generalising that per module is the fiddliest part — but
   only 6 pages and 13 embeds use it today, so it can follow rather than block.
3. **One authored home.** `ModuleSkillProvider` records the constraint: a statically served path
   cannot also hold a durable row. A module-shipped doc must therefore **not** also exist as a node
   file in that package's repo folder. For the 31 of 53 plugins packages that are assembly-backed,
   that is a rule authors must be told; for the 22 pure node packages, `MeshDocs/` does not apply at
   all and the page is simply a node file.
4. **The gates must travel.** This is the one that makes a cheap move expensive, below.

## What is load-bearing where it is

The counter-list, and the half that stops a good idea becoming a bad migration.

**1. The doc tree is gated content, and the gate is core's.** `doc-gate` ("Doc content compiles and
runs") is a required job: it stages `Data/` as a `Doc/` package root, bakes it with `mw-plugin-test`,
then installs, compiles, renders and runs its `Tests` areas. `.github/doc-gate.allow` is **empty** —
zero waivers. On top of that sit `DocumentationCodeBlockCompilationTest` and
`DocExecutableBlocksTest` (which carries a coverage ratchet), `DocExamplesRenderTest`,
`ReactDocViewsTest` — the last with a **hardcoded table of ~20 `Doc/…` paths and their expected
rendered text** — and `WhatsNewEntryIntegrityTest`. A page that moves and leaves its gate behind is
not "moved", it is **unpinned**, and nothing goes red to say so.

**2. `stage-doc-gate.sh` is shared with CD on purpose.** The PR gate and `main-cd`'s `publish-bake`
stage the same tree through one script, *"so the bake publishes exactly what the gate judged."* Any
split of the tree splits that guarantee and must re-establish it on both sides
(see [CI Content Bake](../CiContentBake)).

**3. Eleven `.cs` and seventeen `.json` under `Data/` are invisible to `dotnet build`** — the csproj
removes them from compilation deliberately. `doc-gate` is the only thing that type-checks them, and
`MeshWeaver.Layout.Test` splices two of them into a test assembly by hand.

**4. Ten test projects and one production project reference `MeshWeaver.Documentation`.** The single
non-test consumer in core is `memex/Memex.Portal.Shared`, which is also the sole caller of
`AddDocumentation()` and the sole registrar of `DocumentationStaticRepoSource`. The rest is fixtures:
`MeshWeaver.Content.Test` (7 wiring tests), `Hosting.Orleans.TestBase` — so **every Orleans test
transitively loads the Doc partition** — `MarkdownPipelineBenchmarkTest` (whose corpus *is*
`Data/Architecture`), and `PythonDemo.Test`, which copies one specific page. A doc that is also a
fixture cannot simply follow its module.

**5. Links.** 1 527 internal links, 1 094 of them absolute `/Doc/<Section>/…`; 226 of the 996 files
cross-link each other by node path. Outside the tree, **315 core files (670 occurrences)** name a
`Doc/…` node path and **48 files** name the filesystem path — including all 17 core skill files and
~25 links in `AGENTS.md`. The two are decoupled: preserving the `Doc` partition prefix costs 48
edits; changing it costs 363. Where a page must change path, [Moved-Node Redirects](../NodeRedirects)
is the mitigation that already exists.

**6. 🚨 The dependency runs the WRONG WAY today, and a naive move would deepen it.** The link and
embed integrity gates for *core's* documentation live in the **plugins** repo
(`src/MeshWeaver.AI.Test/DocumentationLinkIntegrityTest.cs` and `DocumentationEmbedIntegrityTest.cs`,
both in `namespace MeshWeaver.Documentation.Test`, both reading
`MeshWeaver.Documentation.Data.` resources). So does `DocumentationBackfill`. Three **production**
plugins projects — `Memex.Portal.Gui`, `Memex.LocalMesh`, `Memex.Database.Migration` — reference
core's `MeshWeaver.Documentation.csproj` across the repo boundary through `$(MeshWeaverRoot)`.

Against the standing rule that *each repo should only use itself*, this is the real defect the survey
found. **Moving AI docs to the AI package removes a cross-repo dependency; moving core's docs out of
core would only reverse one** — and would put a required core gate behind a sibling checkout, which
disqualifies it outright.

## Beyond docs: the content still in core

The doc tree is not the largest content in the platform repo.

**`samples/` — 988 files, 18.9 % of the tree, and CD already refuses to ship it.** `samples/Graph` is
a pure node-content tree (953 files; 112 `.cs`, 556 `.json`) whose csproj sets `ExcludeFromBuild` and
which **nothing references by project name** — it is not even in `MeshWeaver.slnx`. `main-cd.yml`
excludes it from the published bake with the measurement recorded: *"7 packages / 24 assemblies whose
bundles NO deployment can adopt."* Its README already names its consumer as living in the other repo.
It is anchored to core by 13 test projects and one PR gate stage, nothing else.

🚨 **And it contains the same split this page is about.** `samples/Graph/Data/Northwind` is **276
files** of node-native Northwind content in core — while the plugins repo's `Northwind` package is a
**three-file shell** (`index.json`, `Guide.md`, `manifest.lock`) pointing at
`MeshWeaver.Northwind.Application`. One product, two repos, two different artifacts, no gate that can
see both. `FutuRe` (138 files, reinsurance) and `PensionFund` (72) have satellites of their own.

The concrete cost of that content being able to break a framework build is already on record: a
`&euro;` entity inside an XML doc comment in
`samples/Graph/Data/FutuRe/Currency/Source/Currency.cs` — malformed XML (CS1570), warnings-as-errors,
compiled by two independent lanes, and not covered by `samples-gate.allow`.

**`clients/voice-gateway` — 35 files with no CI job at all**, excluded from CD change detection, and
the workflow says so in a comment. A Home Assistant / Ollama voice satellite sitting in the framework
repo, unbuilt and untested.

## The plan, ranked by value over cost

| # | Move | Files | Removes a dependency? | Cost |
|---|---|---|---|---|
| 1 | **Gate the satellite `WhatsNew` lane** | 0 moved | no — closes a hole | one test, ratcheted per repo. Not a move at all, and it fixes a live defect |
| 2 | **`Doc/AI` → the `AI` package** | 13 | **yes** — the docs join the engine | 1 executable block; 63 inbound links need redirects |
| 3 | **Build `ModuleDocProvider`** in `MeshWeaver.Hosting` | — | enables 2, 4 | depth, page assets, and porting the doc gate to modules |
| 4 | **The ~20 module-owned `Architecture` pages** | ~20 | **yes** | per-page; 838 inbound links, so redirects are mandatory |
| 5 | **`samples/` → plugins**, Northwind first | 953 | **yes** — closes the two-repo split | 13 test projects; one PR gate stage must move with it |
| 6 | **`clients/voice-gateway`** | 35 | yes | none measurable — nothing builds it |

**Stays in core, and the reason is the rule, not inertia:** `Architecture`'s reactive, storage,
security, node-type, plugin-system and release pages; all of `DataMesh` bar two; all of `GUI` bar
`React/*`; every `WhatsNew` entry already written. Core is the functionality those pages document.

## Two defects this survey found

**A gate that judges one repo's content from another repo.** `DocumentationLinkIntegrityTest` and
`DocumentationEmbedIntegrityTest` pin core's doc tree from inside `MeshWeaver.Plugins`. Core's own CI
cannot run them, so an ordinary doc edit here can only be judged by a sibling repo's suite — the
exact shape recorded in [Carving Projects Out Of Core](../CarvingProjectsOutOfCore) as the thing not
to add instances of. It was a reasonable consequence of the AI engine's move and it is now load-bearing
in the wrong place.

**A decentral lane with no ratchet.** The satellite `WhatsNew` contract shipped, works, and is
unpinned; three of four entries authored through it are malformed against the very rules the core
test enforces, and the feed renders them in the wrong order rather than failing. A gate copied but
never armed is worse than no gate, because the wall of ticks now includes it.

## Related

- [Carving Projects Out Of Core](../CarvingProjectsOutOfCore) — the SOURCE-move / MODULE-move
  distinction, and what pins each remaining project
- [Authoring Documentation](../AuthoringDocumentation) — the link rules, frontmatter and executable
  blocks any destination must keep honouring
- [Static Repo Import](../StaticRepoImport) — how the embedded tree becomes partition rows
- [Moved-Node Redirects](../NodeRedirects) — keeping 1 094 absolute links alive across a move
- [Plugins](../Plugins) · [Module Build Architecture](../ModuleBuildArchitecture)
