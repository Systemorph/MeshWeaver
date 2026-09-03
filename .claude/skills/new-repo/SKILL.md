---
name: new-repo
description: 'Stand up a NEW repository in the MeshWeaver fleet — a satellite node repo (packages the mesh compiles live) or, rarely, a deployment repo. Use when creating a new Systemorph repo, when adopting the shared CI into an existing one, or when auditing whether a repo is wired correctly (AGENTS.md, scripts/ gates, reusable-workflow CI, publish-bake + the release poll, branch protection, OIDC, .gitignore). Grounded in the six repos that already exist — MeshWeaver.Plugins, .Education, .Reinsurance, .SocialMedia, .Manufacturing, Memex — and in CiContentBake.md + ContinuousDeliveryContract.md.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Write
  - Edit
---

# /new-repo — Stand up a repository in the fleet

**Copy a repo that already works; do not invent a shape.** Six exist today and they agree on far
more than they differ. What follows is the agreement, with each divergence flagged as a decision
you have to make rather than a default you can drift into.

> Canonical references — read the relevant one before wiring the lane:
> - [CiContentBake.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CiContentBake.md) → *"Node repos run the same lane — as reusable workflows"* (the CI contract, the pin rule, the required-check rename).
> - [ContinuousDeliveryContract.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md) → the publication set, the release POLL, and *"Register BOTH OIDC subject formats"*.
> - [AGENTS.md](../../../AGENTS.md) — the fleet's working rules. A satellite does not restate them; it points at them.
> - [`/pullrequest`](../pullrequest/SKILL.md) · [`/plugins`](../plugins/SKILL.md) — the PR gate, and registering the new repo as a catalog source.

## 0. Which kind of repo is this?

| | **Node repo** (the normal case) | **Deployment repo** |
|---|---|---|
| Holds | mesh packages: node-per-file JSON + `Source/*.cs` the mesh compiles at RUNTIME | portal config, per-environment values, migrations |
| Builds | nothing — no `.csproj` unless it ships a compiled module | `.slnx` + images |
| Examples | MeshWeaver.{Plugins, Education, Reinsurance, SocialMedia, Manufacturing} | Systemorph/Memex (**private**) |
| This skill covers | all of it | §11 only |

🚨 **A deployment repo's directory names are tenant identities.** Never copy an environment name,
hostname, customer name or subscription id out of Memex into a public repo, a doc, or a skill.

Everything below is the node-repo lane.

## 1. Create the repo, and the eight files that make it one

`Systemorph/MeshWeaver.<Name>`, default branch `main`, **private unless the content is public**
(Reinsurance, SocialMedia and Manufacturing are; Plugins and Education are too — check before
assuming). Then the skeleton, all copied:

| File | Copy from | Adapt |
|---|---|---|
| `AGENTS.md` | `MeshWeaver.SocialMedia/AGENTS.md` | §2 |
| `CLAUDE.md` | any repo — the 11-byte single line `@AGENTS.md`, uniform across all six | no |
| `README.md` | `MeshWeaver.SocialMedia/README.md` (30 lines: what it is, a module table, a `## Developing` block of three commands, a pointer at AGENTS.md) | yes |
| `.gitignore` | `MeshWeaver.Manufacturing/.gitignore` — 32 lines, the cleanest in the fleet. §9 | small |
| `.github/workflows/ci.yml` | `MeshWeaver.SocialMedia/.github/workflows/ci.yml` | §5 |
| `.github/dependabot.yml` | any node repo — they share a verbatim header and the `github-actions` block | drop the `/e2e` npm block if no e2e |
| `scripts/` | §4 | the `SKIP` set |
| `.claude/skills/{debug,pullrequest,node-files,gates,module-versions}/SKILL.md` | `MeshWeaver.SocialMedia/.claude/skills/` | §2 — all five; the table used to name two and §2 five |

`LICENSE` where the content is licensed (Reinsurance, SocialMedia, Manufacturing carry one;
Plugins and Education do not). No node repo has a root `docs/` — its documentation lives on the
mesh; only the deployment repo has one.

## 2. AGENTS.md — take SocialMedia's, and know what you are taking

**Donor: `MeshWeaver.SocialMedia/AGENTS.md`.** A fleet-wide compaction is in flight — one
`docs/agents-md-factor-out` PR per repo (Education #224, Reinsurance #104, SocialMedia #95,
Manufacturing #26, Memex #142; Plugins uncommitted) moving each file's evidence into on-demand
skills. **Take the post-factor-out form, not what main shows today**, and check whether the PR has
landed before copying.

| repo | AGENTS.md on main | after the factor-out | skills after |
|---|---:|---:|---:|
| Plugins | 135 KB / 1957 L | ~46 KB | 14 |
| Education | 34.6 KB / 512 L | 16.7 KB | 9 |
| Reinsurance | 31.5 KB / 445 L | 19.1 KB | 7 |
| Memex | 27.3 KB / 420 L | 15.1 KB | 4 |
| **SocialMedia** | 20.7 KB / 309 L | **13.6 KB** | **5** |
| Manufacturing | 18.7 KB / 272 L | 12.8 KB | 3 |

SocialMedia wins on the pair, not on size alone: it lands thinnest-but-one **with the complete
skill set** — `debug`, `pullrequest`, `node-files`, `gates`, `module-versions` — and it is the repo
whose CI is the fully-adopted thin caller you are about to copy in §5, so the two halves match.
Manufacturing is 0.8 KB smaller and has **zero unique headings** (the purest skeleton), but it has
no `.claude/` at all and its PR lands without `debug` or `pullrequest` — a gap in the wave, not a
design choice. Take Manufacturing's `.gitignore` and heading skeleton; take SocialMedia's file.

**Its opening move is the thing to copy first**: it does not restate the authoring rules, it
*points at Plugins' `AGENTS.md`* as authoritative and says *"Everything there applies here
unchanged; this file carries only what is specific to this repo."* That is the compaction. A new
repo that restates the plugin mechanism has already forked it. Three blockquote callouts sit under
that H1 in Reinsurance, SocialMedia and Manufacturing near-verbatim — copy all three: the
**upstream-authority pointer**, the **skills-delta note**, and the **system-synced-space access
rule** (on a GitSynced space the only Admin is `system-security`; never hand-create a module's
Space, never grant yourself rights to preview something).

**Fleet-universal — keep these, verbatim in shape:**

| Section | Where |
|---|---|
| `## The instances (which mesh am I talking to?)` | **6/6, heading identical** — which portal each MCP server actually talks to, before any mutation |
| `## Work in worktrees` | **6/6, heading identical.** `git worktree add .worktrees/<name> -b feat/<name> main`; `.worktrees/` gitignored |
| The docs pointer | 6/6 in substance, three headings: `## 📚 Docs live on the mesh` (Reinsurance, SocialMedia, Manufacturing) · `## 📚 Where the documentation lives — search the memex MCP` (Education, Memex) · `## 📚 Read the MeshWeaver docs through the memex MCP` (Plugins) |
| How work ships | 6/6, two headings: `## Git workflow` (Education, Reinsurance, SocialMedia, Memex) · `## Ship it: merge by DEFAULT …` (Plugins, Manufacturing) |

**Node-repo-universal (the four content repos, exact text shared) — copy these too:**

- **Node FILE formats** — `.md` for agents/skills, `.cs` for C# source, `.json` for the rest.
- **Gates (all of them are hard PR gates)** — the §4 list, as a numbered contract.
- **🚨 A folder is addressable only when a node FILE backs it** — `X/` with no `X.json` beside it
  is unreachable.

**Repo-specific — rewrite these, do not copy the content:** `## The modules` (your table),
the body of `## Gates` (yours will match §4), `## Repo-specific scripts`, and anything naming a
partition, a product or a price.

**Two rules are fleet-universal but do NOT live in a satellite's AGENTS.md — and that is
deliberate.** "No band-aids" appears in exactly one of the seven files (core's) and
`-warnaserror` in two (core, Memex — the only repos with a .NET build). A node repo inherits them
by pointing at core, not by restating them. Do not paste them in; do add the pointer.

🚨 **Do not copy SocialMedia's `## Git workflow` paragraph.** It says *"Never commit or push
automatically — wait for the maintainer to ask"*, which contradicts core's AGENTS.md and the
other three node repos. **Take Reinsurance's `## Git workflow`** (*"When the work is finished,
finish it — commit and run `/pullrequest` without being asked"*, plus never-push-to-main and
never-merge-red) **or Manufacturing's `## Ship it: merge by DEFAULT`** (the same policy with an
explicit fatal / system-changing / destructive stop table). Both match the platform.

**Skills — copy five.** Every repo with a `.claude/` carries `debug` and `pullrequest` today
(Plugins, Reinsurance, SocialMedia; Education adds `course-e2e`, `create-space`, `markdown-nodes`);
the factor-out wave converges the node repos on three more: **`node-files`, `gates`,
`module-versions`**. `debug` is effectively byte-shared — its `description` is identical across four
repos and only `allowed-tools` varies. **`pullrequest` is the one you must edit**: each names its own
repo, its own pre-flight gates and its own deploy target. No repo in the fleet has `.claude/agents/`.

SocialMedia's own note says why the files are thin: each *loads the master node from the mesh*
(`get Skill/<name>` over the `memex` MCP) and carries only this repo's delta. The test for a delta:
*would this sentence be true in another repo?* If yes, it belongs in the master, not here.

🚨 **`.claude/settings.local.json` is per-user state and must not be tracked.** Reinsurance tracks
one today (`git ls-files` returns it) — because no node repo's `.gitignore` mentions `.claude` at
all. §9 is the fix.

## 3. Repo layout — one directory per package, node-per-file

A package is a top-level directory whose root is `<Package>/index.json`. Verified shape, from
`MeshWeaver.Manufacturing/Manufacturing/`:

```
Manufacturing/
  index.json            ← the package ROOT: nodeType "Store/Plugin", content $type "PluginContent"
  manifest.lock         ← generated; never hand-edited
  Source/               ← PARTITION-level shared C#, pulled in via `shared=@Manufacturing/Source`
  Station.json          ← a NodeType node       (leaf = X.json; node-with-children = X.json BESIDE X/)
  Station/
    Source/StationLayoutAreas.cs      ← compiled by the MESH at runtime, never by CI's dotnet build
    Test/StationTests.cs              ← singular `Test/`, not `Tests/`
    Test/StationTestsArea.cs          ← backs WithView("Tests", …) — the area the gate EXECUTES
    AnnealingAN1.json …               ← instances of the type
  Agent/…, Skill/…                    ← authored as `.md` with front matter, never escaped JSON
  content/og-card.png, content/videos/…   ← raw assets, never nodes
```

(`MeshWeaver.Plugins/RemoteControl/` is the compact reference for all of it — 19 files, an `Agent/`
and a `Skill/` both `.md`, two NodeTypes with `Source/` + `Test/`, one `UiContribution`, and a
`manifest.lock` short enough to read whole.)

**The root** (`Manufacturing/index.json`) — real key set: `$type: MeshNode`, `id`, `namespace: ""`,
`path`, `mainNode`, `name`, `description`, `nodeType: "Store/Plugin"`, `category`, `icon` (inline
SVG), `state: "Active"`, and `content` of `$type: "PluginContent"` carrying
`requires: ["Publish@^1.0.8", "Store@^1.0.0"]` (a caret pin accepts compatible updates and refuses
a new major; the bare form `["Store"]` stays valid and unconstrained), `version` (the **series**,
e.g. `"1.2"`), `description`, `body` (the Store card's marketing page, ending `@@("area/Search")`),
`price` + `currency` + `contactEmail`, `publicSegments`, `minMeshVersion`, `installPaths`,
`marketingPath`, `tier`, `preInstalled`, and **`entryPoint`**. SocialMedia's adds
`module: "MeshWeaver.Social"` for a **mixed** package (content + a compiled module).

**`entryPoint` is optional** (LinkedIn omits it) and takes three shapes: a plain node path
(`RemoteControl/Start`), a node path whose default view is the app (`Manufacturing/Overview`), or an
explicit layout-area route `{node}/area/{Area}` (`Providers/ProvidersApp/area/ProviderSetup`).
No leading slash.

**A NodeType node** (`Manufacturing/Station.json`) — `nodeType: "NodeType"` and a content of
`$type: "NodeTypeDefinition"` with `description`, `includeGlobalTypes`, `sources`
(`["namespace:Source scope:subtree", "shared=@Manufacturing/Source"]`) and — 🚨 — a
**`configuration` lambda that is C# inside a JSON string**:

```
"configuration": "config => config.WithContentType<Station>().AddDefaultLayoutAreas()
                   .AddLayout(layout => layout.AddStationLayoutAreas().WithView(\"Tests\", StationTestsArea.Tests))"
```

That string is invisible to `grep --include='*.cs'` and to every `dotnet build`. It is why AGENTS.md's
in-mesh-source rule exists and why §4's compile gate is not optional.

**`manifest.lock`** — generated, six keys: `schema: "mw-manifest/1"`, `module`, `files` (**every
file in the folder** — `.json`, `.cs` under `Source/`+`Test/`, `.md`, and `content/` assets — as
repo-path → sha256; 161 entries for Manufacturing), `moduleVersion` (a content **hash** — exact but
unordered, so nothing can pin against it), `version` (**SemVer**, the number dependents pin),
`sourceCommit`. You own MAJOR.MINOR via the root's `content.version`; `gen-manifests.py` derives
PATCH. Never hand-edit it, and 🚨 **never commit a node's `version` field** — that is the owning
hub's persistence counter, and a checked-in one makes `MonotonicWriteGuard` refuse the install
(found in every node repo in 2026-08, counters as high as 4489).

**A menu entry is DATA.** `nodeType: "UiContribution"` — e.g.
`MeshWeaver.Plugins/Approvals/RequestApprovalMenu.json`: `content.$type: "UiContribution"` with
`context` (`"Node"`, `"Admin"` in the live files; the type declares `Node`/`Mesh`/`Settings`/
`AiMenu`/`SidePanel`/`TopBar` — `src/MeshWeaver.Graph/Configuration/UiContributionNodeType.cs`),
`label` + `labelKey` (i18n — supply it), `icon` (emoji), `area`, `href` (`{node}` is the only
placeholder the seam substitutes), `order`, `requiredPermission`. Installing the package adds the
entry; removing it removes it — no platform build. Visibility is enforced by the compiled
aggregator against a closed gate vocabulary; a contribution can only ever NARROW itself. Only four
exist in the whole fleet, all in Plugins — so copy one rather than deriving it.

## 4. `scripts/` — the validators the CI invokes by path

🚨 **The reusable workflows run `python3 scripts/<x>.py` from the CALLER's checkout, by
repo-relative path** (`node-repo-validate.yml:50,55,67`, `node-repo-compile-check.yml:155`,
`node-repo-tag-modules.yml:59`, `node-repo-gate.yml:200`). **There is no shared copy in core** — a
`find` over the platform repo for all fourteen filenames returns zero hits, and
`node-repo-tag-modules.yml` says why: *"scripts/tag-modules.py lives in each node repo, so parsing
its text would couple this shared template to a file it does not own."* Every caller carries its own.

| Script | Carried by | What it asserts | Key flags |
|---|---|---|---|
| `validate-repos.py` | all 5 | node JSON shape + NodeType `Source/` presence, and **fails on any module file hidden by `.gitignore`**. Shape only — it does not compile | none (Plugins adds `--self-test`) |
| `gen-manifests.py` | all 5 | regenerates `manifest.lock`; `--check` = current, `--check-versions` = the derived SemVer matches the content | `--check`, `--check-versions`, `--no-fetch` |
| `compile-check.py` | Plugins, Reins, Social, Manuf | compiles every NodeType's **resolved source set** — concatenated, usings hoisted, **and the `configuration` lambda** — against the framework assemblies exactly as the mesh does on import | `--refs <dir>`, `--image`, `--gen-allow` |
| `tag-modules.py` | all 5 | cuts `<Module>/vX.Y.Z` on main; **verifies rather than moves** a tag that already exists | `--dry-run` |
| `affected-modules.py` | Plugins, Reins, Social, Manuf | the diff → changed modules → transitive dependents → their dependencies, 1:1 with `LocalNodeRepo.CollectDependencies` | `--range`/`--changed`/`--all`/`--self-test`, `--json`, `--root` |
| `store-presentation.py` | Plugins, Edu, Reins, Social | derives the Store checklist: name/description/icon/category/`Store/Plugin`/`content.body`/`@@("area/Search")`/og image, split REQUIRED vs POLISH | `--check`, `--self-test` |
| `check-covers.py` | Plugins, Edu, Reins, Social | no cover is a wall of text — longest **prose** paragraph (structure excluded) | `--max N` (400), `--report`, `--self-test` |
| `dep-graph.py` | Plugins, Reins, Social, Manuf | the package graph from `content.requires`, leaves first | `--mermaid` (Reins: `--check-staged`) |
| `run-node-tests.py`, `gen-scope-proxies.py` | Plugins, Reins | run a module's in-node tests without a mesh; materialise scope proxies (never hand-written) | `--refs`, `--list` / `--check` |

**Exactly three are byte-identical across every repo that has them — copy those verbatim:**
`check-covers.py`, `store-presentation.py`, `compile-check.allow`. Everything else diverges, and
**the divergence is almost always one line: the `SKIP` set** of top-level directories that are not
packages (Reinsurance adds `"legacy"`, Plugins adds `"app"`, Education skips dot-directories).
**Take the newest copy from `MeshWeaver.Plugins`** — it is the hardened superset for every script
except `dep-graph.py --check-staged` (Reinsurance-only) — and edit `SKIP`. Two known-stale copies to
avoid inheriting: SocialMedia's and Manufacturing's `dep-graph.py` read `content.requires` raw, so a
pinned edge `"Store@^1.0.0"` is invisible to them; Manufacturing's `gen-manifests.py` (14 KB vs
~35 KB) derives the patch from tags only, with no `--no-fetch` and no two-witness baseline.

Repo-specific — know they exist rather than copying blind: `check-test-suites.py` (Plugins,
Manufacturing — ratchets `tests=skipped`, which the gate prints *inside* a line beginning `ok`),
`check-ci-invariants.py` + `fetch-registry-package.py` (Manufacturing),
`check-discriminators.py` / `check-scripts.py` / `check-agent-parity.py` / `check-impersonation.py`
(Plugins), `check-course.py` / `check_batteries.py` / `check-examples.py` (Education).

**Allow files are one-way ratchets** — a NEW failure fails the PR; an entry that now passes is
**STALE and also fails** ("remove it — it compiles now"). Who consumes what:

| file | where | consumed by |
|---|---|---|
| `compile-check.allow` | `scripts/` | `scripts/compile-check.py` |
| `plugin-gate.allow` | **repo root** | not a script — passed to `mw-plugin-test` as `--allow`, and it is `node-repo-gate.yml`'s `allow-file` **default** |
| `plugin-tests.allow` | repo root | `scripts/check-test-suites.py` (growth blocked separately via `--base-ref origin/main`) |
| `cover-prose.allow` | repo root | `scripts/check-covers.py` |

🚨 **Seed every one of them EMPTY.** They exist to grandfather debt you inherited; a new repo has
none, and an entry added in the same diff as the hole it excuses is a trapdoor exactly one commit
wide. Where a repo gates additions, the event test lives **inside the step**, never as an `if:`.

Every script that can ships `--self-test`, and CI **runs the self-test immediately before the
check** — because against a clean tree a gate prints the same green tick whether it measures
correctly, measures the wrong thing, or measures nothing at all.

## 5. CI — a THIN CALLER of core's reusable workflows. Never a copy.

🚨 **This is the load-bearing rule of the whole skill.** The shared jobs live in the platform repo
as `workflow_call` workflows. A satellite that hand-rolls or copy-pastes them forks the contract
and stops receiving its fixes.

```
Systemorph/MeshWeaver/.github/workflows/
  node-repo-validate.yml       job: validate       name: Validate node repos
  node-repo-compile-check.yml  job: compile-check  name: Compile every NodeType (vs core)
  node-repo-gate.yml           job: test-repos     name: Compile + render node repos (MeshWeaver from ACR)
  node-repo-tag-modules.yml    job: tag-modules    name: Tag module versions
  node-repo-publish-bake.yml   job: publish-bake   name: Bake + publish NodeType assemblies to portal storage
  node-repo-module-pack.yml    (mixed packages only — builds + packs a compiled module)
  node-repo-platform-ref-bump.yml (opens the pin-bump PR)
```

**Adoption status today, so you copy the right file:** SocialMedia and Reinsurance call the full
five — they are the models. Plugins calls only `module-pack` + `publish-bake` (its gates carry
repo-specific machinery). Education and Manufacturing call none and hand-roll. **Copy
`MeshWeaver.SocialMedia/.github/workflows/ci.yml`.**

A real caller, verbatim from it:

```yaml
  validate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@a727426b09107e2aa6da2fc4f68aae7240fdd6e9

  compile-check:
    needs: [preflight, validate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-compile-check.yml@a727426b09107e2aa6da2fc4f68aae7240fdd6e9
    with:
      test-image:   ${{ vars.MW_TEST_IMAGE }}
      image-digest: ${{ needs.preflight.outputs.image-digest }}
    secrets:
      acr-username: ${{ secrets.ACR_USERNAME }}
      acr-password: ${{ secrets.ACR_PASSWORD }}
```

**The rules the caller must honour:**

- **Pin every `uses:` to a 40-char commit SHA, never `@main` and never a tag.** On `@main` one edit
  to a shared workflow changes every satellite's gates at once and *"did my change break this, or
  did the workflow move under me?"* stops being answerable. A tag is worse: moving it changes all
  satellites with no commit anywhere to attribute it to. GitHub forbids an expression in `uses:`,
  so the SHA is literal on each line — **bump them all in one commit.** SocialMedia's preflight
  reads its own `uses:` lines back and prints the pin's age, shouting past 30 days; copy that step.
- **Pin the platform IMAGE by digest too** (`tag@digest`), and bump it *often*. The pin buys
  reproducibility, but it also caps what the gates can SEE — a pin lagging the platform by days is
  a gate reporting on a framework nobody runs. That is exactly how the 2026-08-09 `AddTracking`
  outage shipped green. An unpinned image is an explicit `allow-unpinned: true`, never a silent
  fallback.
- **Triggers**: `push: [main]`, `pull_request`, `merge_group`, `workflow_dispatch`,
  `repository_dispatch: types: [meshweaver-framework-released]`, and `schedule` (§6).
  🚨 `merge_group` is not optional on a repo with a merge queue — its absence does not look like a
  missing trigger, it looks like a PR that queues and then sits there until it is ejected with no
  failing check to point at.
- **`cancel-in-progress` must be OFF for main pushes**:
  `${{ !(github.event_name == 'push' && github.ref == 'refs/heads/main') }}`. Written as
  push-AND-main, not `github.ref != 'refs/heads/main'` — a scheduled or dispatched run also carries
  the default-branch ref and those must keep superseding each other. Superseding stays on for PR
  branches, which is where the runner saving is. Measured on Plugins with the ref-only form: 18 of
  one day's main runs ended `cancelled`, each killed by the next merge, so the publish jobs never
  ran and the registry served bytes older than main.
- **Keep repo policy in the caller**: the digest pin, the gating (`if:`/`needs:`), the dispatch
  receiver, the module-bundle job, and `scripts/`.

## 6. CD — publish, bake, and the poll that actually delivers

**Publication is `node-repo-publish-bake.yml`**, gated main-only *or* on a release trigger:

```yaml
  publish-bake:
    if: >
      (github.event_name == 'push' && github.ref == 'refs/heads/main') ||
      github.event_name == 'repository_dispatch' ||
      github.event_name == 'schedule'
    needs: [preflight, validate, compile-check, test-repos]
    permissions: {contents: read, id-token: write}   # azure/login OIDC — no long-lived storage secret
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-publish-bake.yml@<sha>
    with:
      bake-source: <lowercase repo short name>       # plugins | reinsurance | socialmedia
      test-image:   ${{ vars.MW_TEST_IMAGE }}
      image-digest: ${{ needs.preflight.outputs.bake-image-digest }}
      bake-publish-targets: ${{ vars.BAKE_PUBLISH_TARGETS }}
      stage-repo: Systemorph/MeshWeaver.Plugins       # only if a package `requires` a module from there
      stage-modules: Store
```

**The digest that the BAKE uses is not the digest the GATES use, and conflating them is the bug.**
Two questions: the gates ask *"do two runs of identical code agree?"* → the pin. The bake asks
*"does the RELEASED framework identity have published bundles?"* → the currently-released image.
SocialMedia's preflight resolves this in a `bake-target` step: on `repository_dispatch`/`schedule`
it resolves `docker manifest inspect -v` → `.Descriptor.digest` and **fails loud** if it gets no
digest, because silently falling back to the pin would republish an identity that is already
published and leave every instance held, under a green tick.

🚨 **`repository_dispatch` is memex's EVENT; the `schedule` is the fallback.** The registry's
`PlatformBuildInboxWatcher` dispatches `meshweaver-framework-released` to every repository the
`Hosting/Deployment` records name as a registry source — so a new repo is subscribed by being named
on a Deployment record (Systemorph/Memex `mesh/Deployments/*.json`, `pluginRepos[]` with
`isRegistrySource`), never by an edit in core. Core CD dispatches to no repository (its
`notify-dependents` was withdrawn 2026-09-03; the 2026-08-22 predecessor printed a not-configured
notice for its whole life while the fleet stayed a release behind, every check green). **A node repo
without `schedule` never bakes for a released identity when a dispatch is lost.** It bakes its pin,
on its own pushes, forever, and the only symptom is an instance HELD on that repo's bundles.
Stagger the minute; observed today: SocialMedia `7,37`, Plugins `17,47`, Reinsurance `22,52`,
Education `32`.

**`FOLLOW_RELEASE` vs the pin, in one line:** `github.event_name == 'repository_dispatch' ||
github.event_name == 'schedule'` — true ⇒ bake the released image; false ⇒ bake the pin, which
certifies the bits the gates just ran.

**Module bundles** (mixed packages only) go through `node-repo-module-pack.yml` with `modules` (a
JSON array of `{package, module, project}`), `platform-ref`, `registry-source`, and a `publish`
expression that is **trunk-only, not push-only** — a release-follow schedule or dispatch MUST
publish or the rebuild it just did is discarded. 🚨 `platform-ref` is a **literal**, not
`${{ env.MW_PLATFORM_REF }}`: the env context is unavailable in a reusable workflow's `with:`. Keep
the `uses:` ref and `platform-ref` the same commit — the lane runs a script from the platform
checkout it makes at `platform-ref`.

## 7. The preflight job — a gate NEVER tests its own inputs

**One job asserts every value that comes from outside the tree, fails RED naming exactly what to
provision, and every gate `needs:` it and runs UNCONDITIONALLY.**

```yaml
  preflight:
    name: Required CI inputs
    runs-on: ubuntu-latest
    outputs:
      # 🚨 TWO outputs, and they are not interchangeable. The GATES read the PIN — an immutable
      # digest every job shares, so one run cannot straddle two images. The BAKE reads whatever
      # the release tag resolves to RIGHT NOW, because its job is to republish against the
      # identity the platform actually released. Defining only `image-digest` leaves the
      # publish-bake job in §6 consuming an output that does not exist: it resolves to the empty
      # string, and the bake targets nothing while the run still goes green.
      image-digest: ${{ steps.pin.outputs.image-digest }}
      bake-image-digest: ${{ steps.bake-target.outputs.image-digest }}
    if: github.event.pull_request.head.repo.fork != true      # the ONLY exemption, on the EVENT
    steps:
      - name: Assert every externally-provisioned CI input is present
        env: {MW_TEST_IMAGE: "${{ vars.MW_TEST_IMAGE }}", ACR_USERNAME: "${{ secrets.ACR_USERNAME }}", …}
        run: |
          set -euo pipefail
          missing=()
          [ -n "${MW_TEST_IMAGE:-}" ] || missing+=("vars.MW_TEST_IMAGE — the mw-plugin-test image reference")
          …
          if [ ${#missing[@]} -gt 0 ]; then
            echo "::error::Required CI inputs are not provisioned, so the gates cannot run. A gate that cannot run fails RED."
            for m in "${missing[@]}"; do echo "::error::missing: ${m}"; done
            exit 1
          fi
```

Why, in three lines: a missing input is a RED job naming what to set, **never a skip**; gates run
unconditionally behind `needs:`; anything genuinely exempt is a check on the **event**
(`fork`, `ref`, `event_name`) — never `if: ${{ vars.X != '' }}`. At job level those two look
identical and only one is safe, because **GitHub renders a skipped job with the same tick as a
passed one**, so "the gate never ran" and "the gate passed" become indistinguishable and the
failure is self-concealing. Not theoretical: Plugins' cross-repo gate was built that shape and
therefore never ran once — #683 deleted a live API and put nine plugin partitions on the
compilation-error overlay in production.

**Adding an input is one line in `missing`.** A staging device (*"stay green until X is wired"*)
that outlives its staging is a silent hole; delete it the day X is wired.

## 8. Branch protection + required checks

🚨 **Adoption RENAMES the required contexts.** A reusable-called workflow's runs report as
`<caller job> / <inner job name>`. Update branch protection **in the same change** — a context left
at the old name waits forever. Verified live today:

| repo | contexts | `strict` |
|---|---|---|
| SocialMedia, Reinsurance (adopted) | `validate / Validate node repos`, `compile-check / Compile every NodeType (vs core)`, `test-repos / Compile + render node repos (MeshWeaver from ACR)` | `false` |
| Plugins, Manufacturing (hand-rolled) | `Validate node repos`, `Compile every NodeType (vs core)`, `Compile + render node repos (MeshWeaver from ACR)` | Plugins `true`, Manufacturing `false` |
| MeshWeaver, Education, Memex | *no branch protection* (core uses a **ruleset** instead — `main pr protection`, id `2128472`: `pull_request`, `copilot_code_review`, `required_status_checks {strict: false, checks: ["Consolidate test results"]}`, `deletion`, `non_fast_forward`) | — |

🔒 A later `@ref` **bump** renames nothing — neither the caller's job id nor the inner `name:` — so
the contexts survive bumps. Only renaming a job in the reusable workflow breaks them, which is why
that rename is a breaking change to every caller.

**`strict` is PER-REPO. Measure it; never remember it:**

```bash
gh api repos/Systemorph/<repo>/branches/main/protection --jq '.required_status_checks.strict'   # 404 = unprotected
```

**Choose `strict: false`.** Plugins is the only `true` in the fleet and it is a merge treadmill:
every merge makes every other branch stale, so each PR needs a fresh CI cycle to catch up. With
`false` a branch that is merely *behind* main merges fine; what you still owe is no conflicts and
a green required check. If you want the combination-that-lands tested, that is a **merge queue**
(`merge_group:` trigger + queue settings), not `strict`.

## 9. `.gitignore` — two ways to lose files silently

**Start from `MeshWeaver.Manufacturing/.gitignore`** (32 lines) and keep its opening banner, which
is the fleet's institutional memory:

> 🚨 THIS IS A NODE REPO — do NOT add .NET build-output rules here. Every top-level module folder is
> a mesh partition of node files, and a node folder may legitimately be named `Release/`, `Debug/`,
> `Bin/` or `Log/`.

**Because git never descends into an ignored directory, a swallowed file is not even reported as
untracked.** It has bitten twice: `[Rr]elease/` hid **77** UWDeepfield node files during the import
into MeshWeaver.Reinsurance, and `[Dd]ebug/` hid the `/debug` SKILL directory in Plugins — `git add`
said *"nothing to commit"*. `validate-repos.py` now fails on any module file hidden by
`.gitignore`; keep that check. If a tree genuinely needs build-output rules, **scope them by path**
(`legacy/**/[Dd]ebug/`, `src/**/bin/`, `test/**/obj/`), never bare.

🚨 **The second way: `.claude/`.** Two opposite mistakes are live in the fleet right now.

- **A blanket `.claude/` means skills can never be committed** — and a doc gate later pointed at the
  skills root would then walk an empty directory and report green having checked nothing. Memex is
  exactly that (`.gitignore:393`); its fix, PR #142, has to change the ignore *and* teach
  `scripts/check-doc-links.py` to walk the skills, in one commit.
- **No rule at all** — the state of *all five node repos* — leaves per-user state committable, and
  Reinsurance has committed `.claude/settings.local.json`.

The correct form, which today exists only in core's `.gitignore` (lines 433-440):

```gitignore
# Claude Code per-user local state (settings.local.json, plans/, scheduled_tasks.lock, …)
# — but DO track the shared team skills under .claude/skills/.
.claude/*
!.claude/skills/
```

Core adds `!.claude/skills/debug/` and `!.claude/skills/release/` **only because it also carries
generic `[Dd]ebug/` / `[Rr]elease/` rules**. A node repo following the banner above needs neither —
but if you ever add such a rule, add the matching negation in the same commit. (Memex's fix branch
adds the two-line form without the per-skill negations, and its lines 16/18 still swallow both.)

Also fleet-universal, present in all six: `.worktrees/`, `node_modules/`, `__pycache__/`, `*.pyc`.
And anchor a nested-repo ignore with a leading slash — an unanchored `plugins/` matches at every
depth and, on macOS's case-insensitive filesystem, also matches `Plugins/`.

## 10. Secrets, variables and OIDC

Provision under **Settings → Secrets and variables → Actions**. Verified names across the fleet:

| | Needed for | Plugins | Reins. | Social | Manuf. | Edu |
|---|---|:-:|:-:|:-:|:-:|:-:|
| `vars.MW_TEST_IMAGE` | the gates (image whose `/app` assemblies are the reference set) | ✓ | ✓ | ✓ | ✓ | — |
| `secrets.ACR_USERNAME` / `ACR_PASSWORD` | `docker login` for that image | ✓ | ✓ | ✓ | ✓ | — |
| `vars.BAKE_PUBLISH_TARGETS` | publish-bake — `<account>/<share>[/<base-path>]`, whitespace-separated | ✓ | ✓ | ✓ | — | ✓ |
| `secrets.AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | `azure/login` OIDC for the publish | ✓ | ✓ | ✓ | — | ✓ |
| `secrets.MESHWEAVER_APP_ID` / `MESHWEAVER_APP_PRIVATE_KEY` | GitHub App that staging a cross-repo module (`stage-repo`) reads with | ✓ | ✓ | ✓ | — | — |
| `vars.MW_REGISTRY_URL` + `secrets.MW_REGISTRY_KEY` | the gate fetching the upstream publication over HTTPS (works on `pull_request`) | — | ✓ | ✓ | — | ✓ |
| `secrets.REGISTRY_PUBLISH_TOKEN` | handing a packed module bundle to the registry | ✓ | — | ✓ | — | — |

**Minimum for a content-only repo: `MW_TEST_IMAGE` + `ACR_USERNAME` + `ACR_PASSWORD`.** Add the
`BAKE_PUBLISH_TARGETS` + `AZURE_*` set the moment you turn on publish-bake — a missing value there
fails the job by design; a grey skip would silently restore every-pod-rebakes-everything.

🚨 **Register BOTH OIDC subject formats — GitHub subjects are immutable, and which form a repo
presents varies per repo inside one org at the same moment.** Measured 2026-08-17: Education and
SocialMedia presented the immutable form while Plugins presented the classic one, so tidying all
four to immutable broke Plugins with `AADSTS700213: No matching federated identity record found`.

```bash
REPO=MeshWeaver.<Name>; SHORT=<short>
REPO_ID=$(gh api repos/Systemorph/$REPO --jq .id); ORG_ID=$(gh api orgs/Systemorph --jq .id)
ISSUER=https://token.actions.githubusercontent.com
az identity federated-credential create -g <aks-resource-group> --identity-name github-actions-bake \
  --name "gh-$SHORT-main-classic" --issuer "$ISSUER" \
  --subject "repo:Systemorph/$REPO:ref:refs/heads/main" --audience api://AzureADTokenExchange
az identity federated-credential create -g <aks-resource-group> --identity-name github-actions-bake \
  --name "gh-meshweaver-$SHORT-main" --issuer "$ISSUER" \
  --subject "repo:Systemorph@$ORG_ID/$REPO@$REPO_ID:ref:refs/heads/main" --audience api://AzureADTokenExchange
```

Expect **two rows per repo**. When `AADSTS700213` appears, copy the presented subject verbatim out
of the error and add a credential for it, **keeping the existing one** — deleting the other format
is what turns another repo's green lane red. Full text:
[ContinuousDeliveryContract.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md)
→ *"Register BOTH OIDC subject formats"*.

Dependabot: `secrets` provisioned for Actions are **not** visible to Dependabot-triggered runs —
provision the pair under *Secrets → Dependabot* too, or a dependabot PR fails preflight.

## 11. Make the content reachable — register the repo as a catalog source

A repo nobody reads ships nothing. The registry holds the git credential and re-serves per
(source, package); consumers speak only HTTP. Add the source in the deployment's Helm values —
which live in the **private** deployments repo, never here:

```yaml
pluginCatalog:
  sources:
    - {name: <Name>, repoPath: "https://github.com/Systemorph/MeshWeaver.<Name>", ref: main}
```

`format` defaults to `node-repo`: a folder appears once it carries a `<Folder>/index.json` root
with a `PluginContent`. Sources merge in configured order, first wins on an id collision, and a
failing repo contributes nothing (logged) rather than breaking the catalog. Grants, tokens and the
anonymous-registry trap: [`/plugins`](../plugins/SKILL.md).

**Deployment repo (§0), in one paragraph:** it is not a node repo and none of §3–§6 applies. It
holds per-environment values and its own workflows (Memex: `build`, `helm-release`, `smoke`,
`deploy-drift`, `config-key-coverage`). Its dependabot watches nuget + docker + actions and
deliberately does **not** bump the portal image pin — the in-pod self-updater chooses the running
build, and a Dependabot PR would fight the reconciler. See [`/new-deployment`](../new-deployment/SKILL.md).

## The checklist

Files and settings:

- [ ] Repo created, `main` default, visibility decided.
- [ ] `AGENTS.md` from SocialMedia, points at Plugins' AGENTS.md as authoritative, `## The modules`
      and `## Gates` rewritten, git-workflow section taken from **Reinsurance or Manufacturing**.
- [ ] `CLAUDE.md` = `@AGENTS.md`. `README.md` ≤ ~40 lines.
- [ ] `.gitignore` from Manufacturing, banner kept, **no unscoped build-output rules**, plus
      `.claude/*` + `!.claude/skills/` (neither a blanket `.claude/` nor no rule at all).
      Prove it: `git check-ignore -v .claude/skills/debug/SKILL.md` must print **nothing**.
- [ ] `.claude/skills/` — `debug` + `pullrequest` (edit the latter: repo name, gates, deploy
      target) + `node-files`, `gates`, `module-versions`; `settings.local.json` NOT tracked.
- [ ] `scripts/` copied from Plugins with `SKIP` adjusted (`check-covers.py`,
      `store-presentation.py`, `compile-check.allow` verbatim); every `--self-test` passes;
      allow files created **empty**.
- [ ] `.github/dependabot.yml` copied.
- [ ] At least one package: `index.json` (`Store/Plugin` + `PluginContent` + `entryPoint`) and a
      `manifest.lock` generated by `python3 scripts/gen-manifests.py`.
- [ ] `ci.yml` copied from SocialMedia; every `uses:` pinned to one SHA; image digest pinned;
      triggers include `merge_group` **and** `schedule`; `cancel-in-progress` off for main pushes.
- [ ] `preflight` names every input, fails red, fork exemption on the **event**; no gate carries an
      input-shaped `if:` or a `continue-on-error:`.
- [ ] Secrets/vars provisioned (§10) for **Actions and Dependabot**; two OIDC federated credentials
      registered.
- [ ] Branch protection: `strict: false`, contexts in the `<caller job> / <name>` form.
- [ ] Registered as a `pluginCatalog.sources` entry in the deployment repo.

**What proves it worked — three observations, not "the files exist":**

```bash
# 1. The first PR's required checks went green under their NEW names.
gh pr checks <n> --repo Systemorph/MeshWeaver.<Name>
gh api repos/Systemorph/MeshWeaver.<Name>/branches/main/protection \
  --jq '.required_status_checks.contexts'          # must MATCH the names above, exactly

# 2. The first main run PUBLISHED — the publish-bake job succeeded and the module tag was cut.
gh run list --repo Systemorph/MeshWeaver.<Name> --branch main --limit 1 --json databaseId,conclusion
gh run view <id> --repo Systemorph/MeshWeaver.<Name> --json jobs \
  --jq '.jobs[] | "\(.conclusion)\t\(.name)"'      # publish-bake + tag-modules present AND success
git ls-remote --tags origin | grep '<Package>/v'   # the version label exists

# 3. A SCHEDULED run has fired and resolved a RELEASE digest, not the pin.
gh run list --repo Systemorph/MeshWeaver.<Name> --event schedule --limit 3 \
  --json conclusion,createdAt,databaseId
```

🚨 **A green tick is not a publication.** Check the jobs, not the run conclusion — a job marked
`continue-on-error: true` fails while its workflow still reports success. And a `preflight` that
went **grey** is the failure this whole file is built around: it means the gates never ran.

## Where the fleet disagrees — decide, don't drift

| Question | The fleet | Take |
|---|---|---|
| Adopt the reusable workflows? | SocialMedia + Reinsurance yes; Plugins partial; Education + Manufacturing no | **Yes.** The two that hand-roll predate the extraction |
| `strict` branch protection? | Plugins `true`; the rest `false` or unprotected | **`false`** — `true` is a measured merge treadmill |
| Commit/push policy in AGENTS.md | 4 repos "finish it / merge by default"; SocialMedia "wait to be asked" | **Finish it** — matches core |
| Where the authoring rules live | SocialMedia/Manufacturing/Reinsurance point at Plugins' AGENTS.md; Plugins holds them | **Point.** A restated rule is a forked rule |
| `.gitignore` and `.claude/` | Memex blanket-ignores it; the five node repos ignore nothing | **Neither** — `.claude/*` + `!.claude/skills/` |
| Which `scripts/` copy | 4-5 variants of most files; Plugins is the hardened superset | **Plugins**, `SKIP` adjusted — not the nearest neighbour |
| `e2e/` suite? | Plugins/Reinsurance/SocialMedia share `00-bootstrap`, `login`, `apps`, `covers`, `helpers.ts`, `packages.ts`; Education has 25 specs; Manufacturing has none | Optional. If you add one, **arm it in CI** — SocialMedia shipped specs nothing ran until an `e2e-static` job was added |
