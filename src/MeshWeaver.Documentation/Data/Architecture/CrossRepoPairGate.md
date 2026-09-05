---
Name: The Cross-Repo Pair Gate
Category: Architecture
Description: A core change that removes public surface can red a plugin repo's trunk hours later, on pull requests that did not make it. The gate that refuses to merge the deleting half until its declared counterpart has landed — what it triggers on, why it reads the API and never checks a sibling out, and where its teeth stop.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 17H7A5 5 0 0 1 7 7h2"/><path d="M15 7h2a5 5 0 1 1 0 10h-2"/><line x1="8" y1="12" x2="16" y2="12"/></svg>
---

# The Cross-Repo Pair Gate

**Ordering is the whole invariant: when a change spans this repository and a plugin repository,
the half that REMOVES public surface must land LAST.**

Until this gate existed, nothing enforced that. The half that landed first decided whether somebody
else's trunk went red, and the failure was invisible on the pull request that caused it: it surfaced
in a *different repository*, on *unrelated* pull requests, minutes to hours later. The people who saw
the red were never the people who caused it.

## What it cost, measured

MeshWeaver#2689 collects five incidents. The first is the shape the gate is built around.

`MeshWeaver#2678` — *"the node-surface views leave the platform for a module"* — merged here and
deleted `ApiTokenLayoutAreas`, `GroupLayoutAreas`, `GroupMembershipLayoutAreas`,
`MeshDataSourceLayoutAreas`, `NotificationLayoutAreas` and `ReleaseLayoutAreas` from
`MeshWeaver.Graph`. Its plugin half — the `MeshWeaver.Graph.Views` module — was still open.
Consequence in MeshWeaver.Plugins:

```
MeshWeaver.AI.Test — 8 failures, all CodeCell*:
  No renderer is registered for area `Content` on hub `rbuergi/cell-…`
```

Those eight failed the `MeshWeaver.AI` module bundle, which failed `All selected bundles built`,
which failed **both** required compile gates. Every open pull request in that repository went red,
and its `main` was red from 17:20 (last success 16:48) until the fix.

The other four shapes on the issue widen the class — an added overload that made a `<see cref>`
ambiguous (`CS0419`, an error under `-warnaserror`); a JSON envelope whose shape three consumers
read by string; a carve-out whose pinned image predated the type it needed; and an explanatory
sentence in a `//` comment that another repository's script parsed as a canonical assembly name.
**This gate addresses the first class only, and says so below.**

## Why nothing else could catch it

| Gate | What it answers | Why #2678 passed it |
|---|---|---|
| [`check-type-forwards.py`](/Doc/Architecture/ModuleVersioning) | can a module ALREADY PUBLISHED still bind this TypeRef? | the nine types were allow-listed as *"proven cross-repo moves nothing binds"* — which was **true**, and irrelevant to whether the other repo's source still compiled |
| its `--sibling` flag | is a departed type a move or a deletion? | CI deliberately never passes it: this repo is PUBLIC, the plugin repos are PRIVATE |
| the plugin repo's own CI | does the plugin tree compile? | it builds against the **published** core package, not core's `main` — green until CD composed the two |
| a binary-compatibility gate | did a name disappear? | nothing was removed in shapes 3–5 at all |

The general statement, and the reason a pair gate is not merely extra diligence: **core's CI does
not build the plugin repos, so the coupling can surface nowhere except CD, after the fact.** No
amount of care in core catches these, because core's gates are not looking at the thing that breaks.

## How it works

Two scripts and one job, `cross-repo-pair` in `.github/workflows/dotnet-test.yml`.

### 1 · The trigger — one detector, reused

`scripts/check-type-forwards.py --surface-json <path>` writes the set of **public top-level types
declared under `src/` at the merge base and no longer declared at HEAD**, in three categories:

| Category | What it is | How it breaks another repo |
|---|---|---|
| `departed` | gone from `src/`, while the assembly it left is still built here | post-#2276 this is what a move INTO a plugin repo looks like from in here — the #2678 shape |
| `moved` | landed in a different `src/` assembly | a type forwarder keeps the type IDENTITY, but the consumer's compile still needs the DESTINATION assembly referenced, or `CS0012` |
| `assembly-left` | the whole assembly is no longer built here | the carve-out wave — the biggest cross-repo pair there is |
| `member-removed` | a public member of a type that STAYS is gone (since #3103) | `CS0117: 'X' does not contain a definition for 'Y'` — #3137, the sixth shape |

That set is deliberately **wider** than the forwarder gate's verdict, because it answers a different
question. In particular the allow file (`scripts/type-forwards.allow`) is **not** consulted: an entry
there states that no shipped module holds the TypeRef — a claim about *binary* compatibility that
says nothing at all about whether the consuming repo's *source* still compiles.

It is also rare enough to be free. Measured on 2026-09-02:

| Window | Public types removed |
|---|---|
| `main~1 … main~25` → `main` | **0** |
| `main~100` → `main` | 116 — all of them the Maps/Indexing carve-out (#2941) |

So an ordinary pull request never meets this gate at all, and the one window that does is exactly
the wave that produced two of the issue's five incidents.

### 2 · The declaration

When the set is non-empty, the pull-request **body** must carry one of:

```text
Pairs-with: Systemorph/MeshWeaver.Plugins#904
Pairs-with: https://github.com/Systemorph/MeshWeaver.Plugins/pull/904
Pairs-with: none — <reason, at least 12 characters>
```

Bulleted and bolded forms are accepted, because that is how a body is actually written. Fenced code
blocks, HTML comments and quoted (`>`) lines are stripped first, so documenting the syntax — this
page included — never declares a pair.

🚨 **A line that starts `Pairs-with:` and parses as neither form is a FAILURE, never an ignored
line.** A typo'd declaration that read as "no declaration" would put the author and the gate in
disagreement about whether a pair was declared, which is the same trapdoor as a gate that skips on
a missing input.

### 3 · The verdict

Each declared counterpart is resolved through the GitHub API and must be **merged into its
repository's default branch**.

- **Not merged** — open, draft, or closed-without-merging — fails. *"Green and open" orders
  nothing*: both halves are then free to merge in either order, and #2678 merged first while its
  counterpart was green. `merged` also subsumes *red*, since a merged pull request passed its own
  repo's gates.
- **Merged into a feature branch** fails too. `MeshWeaver.Plugins#904` — the pull request this
  gate's first incident is about — merged into `feat/collaboration-module`, not `main`. That reads
  as "landed" in every summary view and shipped nothing.
- **A repository outside the fleet register** (`.github/shared-rules.json`) fails; the register is
  the closed set, read at run time rather than hard-coded.
- **An unresolvable counterpart** fails. Present is not valid: 401, 403, 404 and a transport failure
  each get their own sentence, because they take very different fixes.

## Why this is not an inverted dependency

`test/MeshWeaver.Documentation.Test/PlatformNeverDependsOnPluginsGuard.cs` asserts that **the
pull-request gate reaches into no plugin repository**, and this gate sits on the pull-request path.
The distinction that makes both true at once is worth stating precisely, because it is the line the
whole guard is drawn along:

> **A checkout puts another repository's SOURCE into core's build. An API read puts only a FACT
> about it into a verdict.**

This gate resolves a pull-request *number the author declared*, under a scoped GitHub App
installation token with `pull-requests: read`. No plugin source enters core's build; `dotnet build`
here still needs no sibling on disk; a release (`release.yml`) checks nothing out at all. That is the same
footing [`shared-rules`](/Doc/Architecture/SharedRuleBlocks) has stood on since #2732, and both are
inventoried in [Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) § C.

🚨 It is still a real edge, and the cost is stated rather than hidden: **a core pull request can go
red because of a sibling's state.** So the guard now carries a second ledger — `ApiReadLedger` —
enumerating every workflow that reads a sibling through the API, asserted in both directions: a new
one fails naming itself, and an entry that stops matching fails too, because a detector that
silently stops seeing its subject reports a clean tree forever after.

## Where its teeth stop, and what that means

**The gate cannot enumerate a private repository's callers for you.** Core cannot see them, by
construction. So `Pairs-with: none — <reason>` is an escape, and it is deliberately shaped like a
`scripts/type-forwards.allow` entry rather than like a skip: it is a declared, attributable
statement in the pull-request body, it is printed into the job log, and an unexplained `none` is
refused. What the gate removes is the case where **nobody was ever asked**.

It also covers exactly TWO of the seven shapes below — a public type leaving `src/`, and (since
#3103) a public member leaving a type that stays. It does not see the other five, and no detector
can: each of them is detectable only by knowing what the *dependent* consumes.

The structural answer to those is the one #2689 names as its acceptance criterion —
**compile-and-run the dependent's suite against the candidate core commit**, as a CI-time
integration and never as a build-time reference. That keeps the dependency direction intact: core
still builds and ships without the plugin repos present, and the integration is an *observation
about* a candidate commit rather than a *link into* it. This gate is the half of that which core can do alone. The other
half is **event-based and lives in the dependent**: see *"The dependent reacts to core's events"* below.

## The seven shapes

Collected on #2689, #3103 and #3276. Each column says which mechanism sees it — the pair gate here, or the
dependent's own CI reacting to core's release event.

| # | shape | incident | pair gate | dependent's CI on the release event |
|---|---|---|---|---|
| 1 | a public **type** leaves `src/` (departure, forwarded move, whole assembly) | #2678 — nine Graph view classes; Plugins' trunk red two hours | **yes** | yes |
| 2 | an **added overload** makes a dependent's `<see cref>` ambiguous (`CS0419`, an error under `-warnaserror`) | #2678 again, a second independent break from one merge | no — nothing is removed | yes — additions dispatch too |
| 3 | a **JSON envelope's field names** change; the dependent parses them by string | #2689 | no | yes, when the dependent's suite exercises the envelope |
| 4 | a **comment** another repo's regex parses as data | #2689 (2026-09-01 carve-out wave, failure #1) | no | only when the dependent's gate runs on the dispatch — its `validate` lane does not |
| 5 | the **i18n mirror**: a *value* change in `strings.{en,de}.json` (#2650) | every Plugins PR red until the mirror PR lands | no | only when `rn-app` runs on the dispatch — it does not (see the receiver's scope) |
| 6 | a public **member** leaves a type that **stays** (field, const, method, property, event, positional record parameter, enum constant, interface member, nested type) | #3137 — `CacheDuration`/`NegativeCacheDuration`; `CS0117`; `Portal hosts (shard 0)` red on every Plugins PR for three hours, *"nothing was tested"* | **yes** (since #3103) | yes |
| 7 | a public method's **BEHAVIOUR** changes behind an **unchanged signature** — same name, same parameters, different answer | #3276 — `CatalogLayoutAreas.RenderFromSource` began rendering the category landing instead of the flat package list; two `MeshWeaver.PluginCatalog.Test` render tests in Plugins went red, found 48 commits later by a pin bump | no — nothing is added or removed, so no surface detector can see it | **only if the dependent actually rebuilds against the release — a PINNED dependent does not** (below) |

### Shape 7's worst form: the dependent holds a COPY of the test (#3345)

Shape 7 is invisible to every surface detector by construction. It gets worse when the behaviour
that changed is pinned by a test the dependent keeps its **own copy of** — and that is not
hypothetical, it is the arrangement two teardown tests are in today:

| file | lives in |
|---|---|
| `NackReachesTheWaiterDuringTeardownTest` | `test/MeshWeaver.Graph.Test/` **and** `MeshWeaver.Plugins/src/MeshWeaver.Hosting.Monolith.Test/` |
| `LateNackReenqueueTest` | the same two places |

They are duplicated on purpose: core's CI cannot stand up a monolith mesh, so the copy that actually
exercises the behaviour lives in the dependent, and the copy that keeps core honest lives here. What
made #3345 expensive is what held them together — **a comment, on core's copy only**:

> `// Core twin of MeshWeaver.Plugins/… Keep the two in step.`

The person who needed to read that was whoever edited the Plugins copy. The person who saw it was
whoever edited core's. #3291 rewrote core's twins to the no-forced-teardown contract and left the
Plugins originals asserting the contract it had just deleted. Nothing was red — a pinned dependent
does not rebuild on core's release event (see the row above) — until the pin bump, one day later,
produced a 55-second `VERDICT_TIMEOUT` in a suite whose name points at the mesh. It was filed here
as a core regression and bisected across five commits before the premise fell over.

**The control is `TeardownTwinParityTest`, and it lives in the dependent** — the only side that can
see both files, because it builds against `$(MeshWeaverRoot)` at `MW_PLATFORM_REF`. It compares each
body below the `namespace` line against the core checkout **at the pin, never core's `main`**, so it
reddens exactly in the change that MOVES the pin, which is the change that owes the update, and it
is silent in every change that does not. A missing platform checkout FAILS rather than skips.

Two rules follow, and they generalise past these two files:

- **A duplicated test needs a parity guard, not a comment.** Prose that asks a future reader to keep
  two files in step is a control that only fires when someone is already looking at the right one.
- **The marker goes on BOTH copies.** A one-sided note is addressed to the party who does not need
  it. Core's twins now name the guard that enforces them.


### An eighth shape: a `PackageVersion` the satellite consumes VERSIONLESS (#3344)

The seven shapes above are all about *code*. This one is about the **central package list**, and it
is the cheapest of them to trip.

`MeshWeaver.Plugins/src/Directory.Packages.props` **imports this repo's**
`Directory.Packages.props`. A satellite project may therefore carry a versionless
`<PackageReference Include="X" />` whose only version source is an entry here. Delete that entry —
even as collateral in an unrelated withdrawal — and the satellite stops restoring, with
`error NU1010: PackageReference items do not define a corresponding PackageVersion item`.

**Both repos stay green while it is broken**, for two independent reasons:

- nothing in *this* repo consumes the package, so no compile, test or gate here can miss it;
- the satellite pins this repo at `MW_PLATFORM_REF`, so *its* CI still sees the old list — the break
  is invisible there until someone moves the pin, which is a different day and a different PR.

The pair gate does not see it either: nothing public is removed. The first thing that notices is
**`main-cd`**, which is the only lane that builds a checked-out plugins tree against this repo's
list — and by then the damage is that no set seals.

**Measured, #3344 (2026-09-05).** A withdrawal of three SQLitePCLRaw pins also removed
`SQLitePCLRaw.lib.e_sqlite3 3.53.3`, which was not part of that set and had been here since
2026-06-29:

| | |
|---|---|
| #3344 merged | 10:01:41Z (every check green) |
| main-cd #7813 failed | 10:05:09Z — `NU1010`, `Plugins: bake + seal` **skipped** |
| last sealed set | #7811, 09:27:02Z |

It was also the CVE remedy for GHSA-2m69-gcr7-jv3q, so the same line drop reintroduced a
high-severity advisory as `NU1903`. The comment block above the entry survived the removal intact
and still read *"the pin below"* and *"The pin is the source of truth"* — pointing at a line that
was gone, which is what made the deletion read as deliberate in review.

**How big is it?** Measured on `main`, 2026-09-05: MeshWeaver.Plugins carries **49** versionless
`<PackageReference>`s that no project in this repository references at all, and **47** of those
resolve their version from an entry here. Any one of them can be deleted with every core check
green, and Plugins stops restoring.

**There is NO guard for this yet, and a list would be the wrong one.** The obvious control — naming
the load-bearing entries in a test here — was written, measured against that number, and discarded:
a hand-maintained list of 47 goes red on core PRs whenever *Plugins* legitimately drops a
dependency, taxing every unrelated change in this repo for a fact that lives in another one.

The control that fits the shape is a **restore of the satellite tree in core's PR lane** — the same
`actions/checkout` of `Systemorph/MeshWeaver.Plugins` that `main-cd` already does, followed by a
`dotnet restore` of the projects that import this file. It is cheap (the failure is a restore
diagnostic, not a build) and it derives the answer instead of remembering it. Until it exists,
**this shape is uncovered**: when you remove an entry from `Directory.Packages.props`, grep the
satellite for it by hand —

```
grep -rn 'PackageReference Include="<id>"' ../MeshWeaver.Plugins/src/*/*.csproj
```

— and treat a versionless hit as a blocker.


## Member-level detection (the sixth shape)

`check-type-forwards.py` indexes, under each public top-level type, the **names** of its public
members: body members one indent level inside the type that say `public` (or, in an interface,
that do not say otherwise; every enum constant), plus a record's positional parameters — those ARE
public properties, and renaming one breaks every `with { X = … }` in a consumer. Nested public
types count as members of their outer type; a constructor is `.ctor`; an indexer is `this[]`; an
operator is `operator <token>`.

A member removed from a type that is still declared at HEAD is reported as `member-removed` with
`fullName` `Namespace.Type.Member`. A removed type is reported **once** — its members do not pile
on. Two deliberate limits: the granularity is the NAME, so removing one overload while another
still binds is not reported; and a rename is a removal plus an addition, which is what it is to a
consumer.

Measured 2026-09-03:

| what | result |
|---|---|
| `src/` at `main` | 1 850 public top-level types, **11 574 public members** across 35 assemblies |
| #3137 replayed (`--base e4ab72222^1 --head e4ab72222`) | exactly the two fields, no other entry |
| `main~25 → main` | 2 members removed (both #3137); 13 types and 17 members added |
| per merge, last 25 | 21 touch a `src/*.cs`; **11** change the public declaration set; **1** removes from it |

The control arm grows with it: the report now carries `publicMembersAtBase` beside
`publicTypesAtBase`, and the dispatcher below refuses a report that saw fewer than 3 000 members.

### A waiver must rest on a sweep that ran

AGENTS.md asks for a `search_chunks` sweep of the live mesh before deleting public surface, because
in-mesh source is invisible to every compiler. #3137's pull request made that sweep, the deployment
answered `"searched": false` — no embedding provider, nothing searched (#2741) — and the answer was
read as "no callers". So a `Pairs-with: none — <reason>` whose reason contains `searched: false` is
now **refused**, and a reason that mentions a sweep (`sweep`, `swept`, `search_chunks`) without
quoting `searched: true` is refused too. A reason that rests on something else — *"only read by the
test this PR rewrites"* — is judged on its length alone, as before.

## The dependent reacts to core's events — core never waits

**Rule (maintainer, 2026-09-03): none of the top-level repositories depends on another, and the
integration between them is event-based.** Core *emits*; a dependent *reacts* in its own CI, and
the red lands in the repository that owns the fix.

The event source is the MESH. The target shape (maintainer, 2026-09-03): **memex issues an event
that something has a new version** — a platform build landed in `Hosting/PlatformBuilds`, a module
bundle was published to the registry — and **the GitHub repositories subscribe to it** and trigger
their builds. The emitter IS memex (since 2026-09-03): core's CD POSTs the signed build fact into
`Hosting/PlatformBuilds` and finishes; the Hosting module's `PlatformBuildInboxWatcher`
(MeshWeaver.Plugins) fans `meshweaver-framework-released` out to every repository the
`Hosting/Deployment` records name as a registry source — data in the mesh, not a list in any
workflow. Core's own `notify-dependents` dispatcher, the last CI-to-CI link between repositories,
was withdrawn the same day, and `PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository` refuses a
`/dispatches` POST in any workflow core runs on its own behalf. Each dependent's `ci.yml`
receives it, resolves its `platform-ref` to that release, builds its `src/` and content against it,
runs its suites and — only if everything passes — seals and publishes its bundles for that platform
identity. A shape-1…7 break therefore surfaces as a red release-follow run **in the dependent's
repository**, minutes after the platform published, with the dependent's own test names in the
log, and it is fixed there by a pull request in that repository. Nothing in core polls, reads back
or blocks on it.

🚨 **…as long as the dependent's source lane actually resolves to the released commit. A PINNED
dependent does not, and #3276 is the measurement.** MeshWeaver.Plugins pins core deliberately
(`MW_PLATFORM_REF`, its own #1255: a floating `main` reddened that repo three times in one day with
no diff in any branch there), and its `platform-ref` job resolves the SAME pin on a
`repository_dispatch` release event as on a pull request — the event changes what is baked, not what
`src/` compiles against. So the release-follow run rebuilds the dependent against the commit it
already used, and a break introduced after the pin is invisible to it. It waits, silently, until a
human bumps the pin — and then arrives as *"my pin bump is red"*, which reads like the bumper's
problem and is not.

That is not an argument for unpinning: the pin was bought with a real incident and it moves the
break to a diff the dependent owns. It is an argument for reading the two halves as what they are —
**the event catches a break for a dependent that TRACKS; for a dependent that PINS, the pin bump IS
the integration test**, and the cost of a break is proportional to how long the pin sat. #3276 sat
for 48 commits, and the whole cost was paid by the bisect that found it.

**The contract (maintainer, 2026-09-03: *"end of github pipeline must call memex, which must
register release and publish event"*) is three sentences:**

1. **Every publishing pipeline ENDS with one call to memex.** Core's CD, after the image set is
   promoted, POSTs the signed platform build (`event: platform-build`) into the control instance's
   `Hosting/PlatformBuilds` inbox (`notify-platform-update`). Every node repository's
   `node-repo-publish-bake.yml` run, after its bundles are sealed for an identity, POSTs the signed
   publication record (`event: bundle-publication` — source, identity, commit, tester + portal image)
   into the same inbox (`register-publication`, its last job). Nothing runs after that call, and no
   pipeline sends a `repository_dispatch` to another repository.
2. **memex REGISTERS the release** as a durable node — `Hosting/PlatformBuilds/<version>` for a
   platform build, `Hosting/Publications/<identity>/<source>` for a bundle publication — the source
   of truth for "what is published for which identity" (what the self-update availability check reads).
3. **memex PUBLISHES the event** from that registration: `FrameworkReleaseBroadcaster` sends
   `meshweaver-framework-released` (platform) or `meshweaver-upstream-published` (bundle publication,
   `client_payload.version` = the identity) to the subscribed repositories — the repositories the
   control instance's `Hosting/Deployment` records name as registry sources. The subscribers' CI
   receives it, resolves both images from the version, builds and publishes for that identity — and
   ends by calling memex (1).

```
 pipeline (core CD | a node repo's publish-bake)        memex (control instance)              subscriber CI
 ───────────────────────────────────────────────        ────────────────────────              ─────────────
 promote / seal ✅                                       WebhookInbox Hosting/PlatformBuilds
   └─ ONE signed POST ──(platform-build |──────────────▶│ verify HMAC
      bundle-publication)… and FINISH                    ├─ REGISTER  Hosting/PlatformBuilds/<version>
                                                         │            Hosting/Publications/<identity>/<source>
                                                         ├─ subscribers = Hosting/Deployment records'
                                                         │              pluginRepos[].isRegistrySource
                                                         └─ PUBLISH   repository_dispatch ─────────────▶ on: repository_dispatch:
                                                            meshweaver-framework-released |               types: [meshweaver-framework-released,
                                                            meshweaver-upstream-published                        meshweaver-upstream-published]
                                                                                                          → bake for the version → seal → POST memex
```

Where the pieces are: the POST steps in `main-cd.yml` and `node-repo-publish-bake.yml` (this repo);
the inbox watcher, registration and broadcast in the Hosting module's `PlatformBuildInboxWatcher`
(MeshWeaver.Plugins, `Hosting/Deployment/Source`); the broadcaster in `src/MeshWeaver.GitSync`.
`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository` refuses a dispatch SENDER in any workflow
under `.github/workflows` — there is no ledger — and
`UpstreamBuildGateGuard.TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody` pins the lane's call.

What this deliberately does NOT do: put a context on the core pull request that only a plugin
repository can turn green. A dispatcher of that shape (`dependent-suites.yml`, `core-pr-suites`) was
built for #3103 on 2026-09-03 and withdrawn the same day — it coupled the two trunks (every core
pull request went red until a receiver existed in the plugin repository), and a synchronous wait on
a sibling repository is a dependency whatever token it uses.
`PlatformNeverDependsOnPluginsGuard.ApiReadLedger` therefore lists only the two read-only edges
core keeps (the shared-rules sweep and the `Pairs-with:` resolution) and refuses a third.

The consequence the pair gate covers stays: a pull request that REMOVES public surface must name
its merged counterpart. Everything additive or behavioural (shapes 2–5 and 7) is the dependent's to
catch when it next builds against the platform — on the event if it tracks, on the pin bump if it
pins.

## Proving it

Both scripts run `--self-test` **first** in the job, and both fail it:

- `check-type-forwards.py --self-test` — 49 cases (29 forwarder verdict + 20 surface report). The
  surface cases prove the report fires on a departure, on a **forwarded** move (which the verdict
  half is correctly silent on), on a whole assembly leaving, and — the sixth shape — on #3137's own
  text in miniature, a renamed method, a member made `internal`, a renamed positional record
  parameter, a removed enum constant, a removed interface member and a block-scoped namespace; and
  stays silent on a within-assembly file move, an internal type, an in-mesh doc sample, an
  addition, a body edit and a removed overload whose name still binds.
- `check-cross-repo-pair.py --self-test` — 28 cases, including the passing ones. A gate that always
  failed would score identically without them. Five prove the member and sweep rules above.

The control arm is `publicTypesAtBase`. Every other field of the surface report is legitimately
empty on an ordinary pull request, so *"this diff removed nothing"* and *"the scan read nothing"*
would otherwise produce the same JSON. The gate refuses a base tree declaring fewer than 500 public
top-level types; `src/` declares 1832 across 35 assemblies today.

## See also

[Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) ·
[Shared Rule Blocks](/Doc/Architecture/SharedRuleBlocks) ·
[Carving Projects Out Of Core](/Doc/Architecture/CarvingProjectsOutOfCore) ·
[Module Versioning](/Doc/Architecture/ModuleVersioning)
- [Image Pair Skew](/Doc/Architecture/ImagePairSkew) — the same class at the IMAGE level: CD pairs a
  core commit with a Plugins head resolved hours later, and the pair is never executed before it is
  promoted.
