---
Name: The Cross-Repo Pair Gate
Category: Architecture
Description: A core change that removes public surface — or ADDS a member to an interface someone else implements — can red a plugin repo's trunk hours later, on pull requests that did not make it. The gates that refuse to merge such a change undeclared, what each triggers on, why they read the API and never check a sibling out, and which of the ten shapes still have nothing.
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

It also covers exactly TWO of the ten shapes below — a public type leaving `src/`, and (since
#3103) a public member leaving a type that stays. Two more have gates of their own, and **four have
nothing**. The whole board, so that "uncovered" is written down rather than inferred:

| # | shape | gated by | status |
|---|---|---|---|
| 1 | a public **type** leaves `src/` | `Cross-repo pair (public surface)` | **gated** |
| 2 | an **added overload** makes a dependent's `<see cref>` ambiguous | — | **UNCOVERED** |
| 3 | a **JSON envelope's field names** change | — | **UNCOVERED** |
| 4 | a **comment** another repo's regex parses as data | — | **UNCOVERED** |
| 5 | an i18n **value** change | — | **UNCOVERED** (the mirror guard compares against a *pinned* core commit) |
| 6 | a public **member** leaves a type that stays | `Cross-repo pair (public surface)` | **gated** |
| 7 | a public method's **BEHAVIOUR** changes behind an unchanged signature | — | **UNCOVERED**, and undetectable by construction |
| 8 | a `PackageVersion` a satellite consumes VERSIONLESS is removed | `Satellite package pins (removal declared)` | **gated** (#3349) |
| 9 | a RENDERED-UI change a satellite's e2e asserts on | — | **UNCOVERED**; the honest instrument is the release note |
| 10 | a member **ADDED** to a public interface breaks external IMPLEMENTERS | `Interface additions (implementers declared)` | **gated** (#3465) |

Shapes 2–5, 7 and 9 are detectable only by knowing what the *dependent* consumes, and core cannot
know that.

The structural answer to those is the one #2689 names as its acceptance criterion —
**compile-and-run the dependent's suite against the candidate core commit**, as a CI-time
integration and never as a build-time reference. That keeps the dependency direction intact: core
still builds and ships without the plugin repos present, and the integration is an *observation
about* a candidate commit rather than a *link into* it. This gate is the half of that which core can do alone. The other
half is **event-based and lives in the dependent**: see *"The dependent reacts to core's events"* below.

## The seven CODE shapes

Collected on #2689, #3103 and #3276. Each column says which mechanism sees it — the pair gate here, or the
dependent's own CI reacting to core's release event. Three more shapes were found later and have
sections of their own below: the **eighth** is a central package pin (#3344), the **ninth** a
rendered-UI string a satellite's e2e asserts on (#3401), and the **tenth** an *added* interface
member that breaks external implementers (#3465).

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

### Shape 7 in a memory bound: #4017 and `NodeTypeRecompileAlcLeakTest` (2026-09-11)

Core #4017 (`b128b804d`) changed WHEN a NodeType load context is superseded — only a publish, never
a read — behind an unchanged `ICompilationCacheService.GetOrCreateLoadContextForPath` signature. The
pair gate was green and correct to be green: nothing was added or removed. The behaviour it changed
is asserted in the dependent, by `NodeTypeRecompileAlcLeakTest.RecompilingANodeType_WithALiveInstance_StillReleasesSupersededContexts`
(Plugins, `Portal hosts (shard 3)`), and Plugins resolves the newest SEALED core set per run — so the
red arrived when the set carrying #4017 sealed, on every Plugins PR and on Plugins `main`, with no
Plugins change:

> `Expected 3 to be less than or equal to 2 because a live instance hub legitimately DEFERS an unload, but nothing may make that deferral permanent: …`

**The control that settled causation held the Plugins diff constant and moved only the core set:**
the same Plugins commit `401fcadb` passed shard 3 at 09:56Z on `3.0.0-ci.8340` and failed it at
10:09Z on `3.0.0-ci.8345`. Across the 42 shard-3 runs between 05:10Z and 11:32Z the test failed 6 of
6 on sets 8345/8350/8352 and 0 of 36 on 8323–8340, and #4017 was the only merge between 8340 and
8345 touching compilation code. The deciding question was then not *who* but *which side is
wrong*: the assertion was right (the read path had begun keeping a second context over the same
build), so core was fixed forward and the test kept its bound — see
[NodeTypeCompilation](../NodeTypeCompilation) → *One build at two paths is ONE generation*.

Two lessons for this shape. **The dependent resolves the SEALED set, so a shape-7 red surfaces at
seal time, not merge time** — attribute by the set each run's `Resolve the released platform` job
names, never by the clock. And **a red in the dependent is evidence about core only after the diff
is held constant** — one commit, two sets.

### 🚨 Shape 7 in the by-hand sweep: a `!` on the WRONG receiver hides the site (#3321)

Shape 7 has no gate, so the only control is a **by-hand sweep of the dependents**. This is about how
to write that sweep, because #3321 found a shape that defeats the obvious way of doing it.

Core's step 3 made `ISynchronizationStream.Hub` — a public property, declared **non-nullable** —
start answering `null` once a stream releases it. Nothing was added or removed, so the gate saw
nothing, correctly. The sweep found **11 production dereferences** in `MeshWeaver.Plugins`'s Blazor
view layer, every one of them written like this:

```csharp
// BlazorView.Stream is `ISynchronizationStream<JsonElement>?`; Hub is declared non-nullable
Stream!.Hub.Post(new ClickedEvent(Area, Stream.StreamId), …);
je.Deserialize<TValue>(Stream!.Hub.JsonSerializerOptions);
```

🚨 **The `!` binds to `Stream`, not to `.Hub`.** It suppresses the warning about the receiver the
compiler already knew was nullable, and says nothing whatever about the dereference one level to the
right — which is the one that changed. So when `Hub` becomes absent, the NRE lands past the operator
that looks like it was put there to handle exactly this, and **no `CS8602` is ever emitted**. A
reviewer skimming for "unguarded dereference" reads `!` as due diligence.

Two of the 11 were worse than silent:

- one sat inside a bare `catch { }` (`RadzenChartView.razor`), where the new NRE would have been
  swallowed and the chart rendered from **unconverted data** — wrong output, no exception, no log;
- one in **core itself** (`WorkspaceExtensions.ApplyChanges`, `stream!.Hub.Version`) was missed by
  the previous step's own site census for the same reason, and only a fresh sweep found it.

**The rules for a shape-7 sweep, and they generalise past this property:**

- **Resolve the RECEIVER's type; never grep the member textually.** `grep '\.Hub'` over
  `MeshWeaver.Plugins` returns 2 051 lines, of which 1 552 survive a word-boundary filter and
  **22** are actually a stream's. The rest are `IWorkspace.Hub`, `LayoutAreaHost.Hub`, the
  `MeshWeaver.Messaging.Hub` *namespace*, and `.HubConfiguration`. A name heuristic cannot do this;
  something has to read each distinct receiver's declaration.
- **A `!` anywhere in the expression is a reason to look harder, not a reason to skip.** It is
  evidence that *some* nullability was considered — which is not the same as the one you changed.
- **Sweep every file type, not just `.cs`.** In-mesh C# lives inside `.json` node strings; 27 such
  nodes carried `.Hub` (all benign here, but only reading them establishes that).
- **Count at every stage, and treat a suspiciously round zero as a broken sweep.** The first pass of
  #3321's sweep returned `0` in every repo because an unquoted glob killed the command under zsh.
  The clean answer and the never-ran answer look identical.

The measured blast radius — six satellite checkouts, every file, receiver-resolved — was **22
occurrences in one repo and zero in the other five**, with the four content satellites clean
*structurally* (`ISynchronizationStream` appears in zero files in four of them). The landing order
that follows from shape 7 having no gate is in
[Stream Liveness and the Hub Reference](/Doc/Architecture/StreamLivenessAndTheHubReference):
**the dependent's guards are no-ops against the old core, so the dependent merges FIRST.**

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

**Two obvious controls are wrong, and knowing why is what makes the third one right.**

*A list of the load-bearing entries* was written, measured against that number, and discarded: a
hand-maintained list of 47 goes red on core PRs whenever *Plugins* legitimately drops a dependency,
taxing every unrelated change in this repo for a fact that lives in another one.

*A restore of the satellite tree in core's PR lane* — `actions/checkout` of
`Systemorph/MeshWeaver.Plugins` followed by a `dotnet restore`, the way `main-cd` does — derives the
answer instead of remembering it, and is the control this page recommended until #3349.
**It cannot be built here.** `PlatformNeverDependsOnPluginsGuard.ThePullRequestGate_ReachesIntoNoPluginRepository`
asserts that `dotnet-test.yml` contains zero actionable cross-repo hits, and both
`repository: Systemorph/MeshWeaver.Plugins` and any line reading `plugins-repo` are hits. That ban
is the point rather than an obstacle: a gate on core's own pull requests whose verdict depends on a
sibling's moving HEAD makes the *same* diff go red or green with no change of its own, and re-adding
a checkout silently restores the external input that workflow was deliberately cleared of.

### The gate that ships: declare the removal (#3349)

`Satellite package pins (removal declared)` fires **only when the diff removes a `PackageVersion`**
and requires the pull-request body to name each removed id:

```
Satellite-pins: <PackageId>[, <PackageId>…] — <what you checked and what you found>
```

It is the same instrument this page already describes for public surface, applied to the same shape
one category over: a package pin is a `Pairs-with:` case that happens not to be C#. Core-only — no
checkout, no API read, no credential, no ledger entry, nothing that can go red on somebody else's
HEAD — so it also runs on **fork** pull requests, where the credentialed gates cannot.

**What it does not do, stated plainly:** it does not know whether a removed pin is load-bearing. It
makes a human find out, with the grep below, and say what they found. That is weaker than deriving
the answer and is the honest price of staying inside the dependency direction. There is deliberately
**no blanket form** — *"none of them matter"* is the sentence that produced #3344.

```
grep -rn 'PackageReference Include="<id>"' ../MeshWeaver.Plugins/src/*/*.csproj
```

A versionless hit is a **blocker**: keep the entry, or remove the reference there first.

**Proven against the incident, not just against fixtures.** Replaying #3344's own commit
(`c08fee100`) with its three *intended* withdrawals declared, the gate reds naming exactly the
fourth:

```
Undeclared removal(s):
  • SQLitePCLRaw.lib.e_sqlite3
```

That is the collateral removal that cost ~80 minutes of sealing nothing and reintroduced the CVE.
An **undecidable** read — a shallow fetch missing the merge base, a moved props file, a matcher that
stopped matching — exits RED naming why, never "nothing was removed": *couldn't tell* and *clean*
must never be one colour, which is the failure that let #3344 through with every check green.


### A ninth shape: a RENDERED-UI change the satellite's e2e asserts on (#3401 fallout, 2026-09-06)

Shape 5 is about the i18n **mirror** — a value change in `strings.{en,de}.json` reddening
MeshWeaver.Plugins' catalog guard. This is the other end of the same wire: the value reaches a
**satellite's browser tests**, which assert on rendered text and rendered navigation. Nothing sees
it. Not the pair gate (nothing public is removed), not the mirror guard (that compares catalogs, not
pages), not the satellite's own CI (it pins the platform, so it keeps passing until someone moves
the pin — a different day and a different PR).

**One core commit produced TWO independent breakages in MeshWeaver.Education, and the first hid the
second.** `c521f929e` (2026-09-03) — *"the plugin catalog opens with categories and loads packages
per category"*:

| | what changed | how it surfaced |
|---|---|---|
| a | `package(s)` → the localized plurals `plural.package.one` / `plural.package.other` | `TEXT.catalogAvailable`, the literal `'package(s) available'`, matched nothing. Bootstrap waited 20 s for text the page will never show again — while the page read `… — 12 packages available.` |
| b | the catalog now opens on a **category index** and loads cards per category | with (a) fixed, it failed later and faster (9.8 min → 4.9 min): `the Plugin Catalog has no card titled 'Store'`, on a page whose only children were two links, `Uncategorized` and `All packages` |

Both look like the satellite is broken. Neither is. And (b) was **unreachable** until (a) was fixed,
so a single round of debugging finds one wall and reports success prematurely.

**Why the pin makes it worse, not better.** Education's platform pin predated `c521f929e` by days,
so its e2e stayed green the entire time the break existed. The break arrived with the *pin bump* —
authored by someone fixing an unrelated purged-manifest problem — which is the worst possible moment
for it, because the bump's own diff is three digests and cannot plausibly be the cause. Expect the
report to be *"the pin bump broke the e2e"*.

**What to do about it, on both sides:**

- **Core:** a rendered-string or navigation change is a cross-repo change even though nothing public
  moves. It cannot be gated from here — no core test knows what a satellite asserts on — so the
  honest instrument is the release note, not a checker.
- **Satellites:** do not assert on literal platform UI text. Education's fix was a regex tolerating
  singular, plural *and* the retired `package(s)` form, plus a `openCatalog()` helper whose category
  click is **conditional** — so the specs pass against a platform on either side of the change
  rather than being pinned to one build. That property is what makes the next bump cheap.

🚨 **Reading the failure is what shortens this.** Both walls were diagnosed from the Playwright page
snapshot in the run's artifact, which showed the catalog rendering correctly in both cases. Two
plausible causes taken from the container log — Orleans `no active nodes … grain podhub`, and
`ConfigMasterKeyProvider` refusing without a master key — were both **excluded by evidence**: the
podhub errors appear in the last PASSING run too, and that refusal landed ten days before it. Guess
from a log and this costs a day; read the snapshot and it costs an hour.

### A tenth shape: an ADDED interface member breaks external IMPLEMENTERS (#3465, 2026-09-06)

Everything above triggers on something LEAVING. This one is the mirror image, and it is the half
nothing covered:

> 🚨 **A forwarder rescues a CALLER. It cannot rescue an IMPLEMENTER.**

Core **#3446** (the #3433 EA credential seam) added three reactive members to `IEaGraphAuth` —
`GetConnection`, `GetAccessToken`, `ExchangeAndStore` — and kept the retiring `…Async` surface as
**default-implemented forwarders**. Every CALLER therefore kept compiling. The pair gate was
correctly silent, because nothing was removed. Core `main` went green. Then MeshWeaver.Plugins moved
its platform pin onto the sealed set carrying it (Plugins#1415):

```
src/MeshWeaver.Mail.MicrosoftGraph.Test/ExecutiveAssistantDraftLifecycleTests.cs(438,44):
  error CS0535: 'FakeEaGraphAuth' does not implement interface member 'IEaGraphAuth.ExchangeAndStore(string, string, string)'
  error CS0535: ... 'IEaGraphAuth.GetAccessToken(string)'
  error CS0535: ... 'IEaGraphAuth.GetConnection(string)'
```

`FakeEaGraphAuth` is a sanctioned test double that **implements** the interface, so adding a member
obliges it to supply one, and no amount of source-compatible forwarding on the core side changes
that. **Callers and implementers have different compatibility rules, and only the caller half was
ever covered.**

🚨 **#3446's forwarder strategy was CORRECT and is not the mistake.** It is what kept every caller
working and what made the core change safe to land on its own. It is also what the platform's own
rules ask for — AGENTS.md says retire a published symbol as a forwarder, never a delete — and it is
deliberately **not** `[Obsolete]`, because MeshWeaver.Plugins builds `-warnaserror` against a core
checkout at a pinned ref, so the attribute would red that repo's `main` the moment core merged. The
gap is the shape, not the strategy.

**Why it bit harder than usual: a CIRCULAR block on the release critical path.**

- Plugins#1415 (the pin move) was red, because `FakeEaGraphAuth` predates #3446.
- Plugins#1416 (the adaptation that fixes it) was correctly held as a **draft**, because it cannot
  compile against the OLD pin.

Each blocked on the other, Plugins `main` was dark for hours, and three sessions were needed to
unwind it. The resolution is that the two must land together — but nothing warned that they would
have to, and **the coupling was discovered by a red rather than predicted**.

**Why the existing partial cover cannot fire either.** Same reason this page already records for
shape 7: *for a dependent that PINS core, the pin bump IS the integration test.* Plugins'
`platform-ref` job resolves the same pin on the release event as on a pull request, so the event
moves what is BAKED, not what `src/` compiles against. The break is invisible until somebody moves
the pin — which is when it is dearest.

#### The gate that ships: declare the addition

`Interface additions (implementers declared)` fires **only when the diff adds a member an outside
implementer would have to write**, and requires the pull-request body to name each one:

```
Implementers: <Type.Member>[, <Type.Member>…] — <what you checked and what you found>
```

A member counts as *implementer-obliging* when:

- it is added to a **public interface** that already existed at the merge base, is public (an
  interface member is, unless it says otherwise) and carries **no body** — no `=>`, no `{ … }`
  block, an accessor list of bare `get;`/`set;`/`init;` only — and is not `static` unless it is also
  `abstract`; or
- it is an **`abstract`** member added to a **public abstract class** that is not `sealed` —
  `CS0534` in an external subclass rather than `CS0535`, same break, same declaration.

🚨 **Giving the member a DEFAULT IMPLEMENTATION silences the gate, and that is the point.** A default
interface member keeps every implementer compiling, so it is the actual fix; a gate that taxed it
would push authors away from the one change that works. #3446's `…Async` forwarders are exactly that
shape, and the detector is silent on them — including the case where the `=>` sits two lines below
the parameter list, which is how a long signature is naturally written.

#### 🚨 The ordering is INVERTED, and that is why this is not a `Pairs-with:`

For a REMOVAL the deleting half lands **last**, so `Pairs-with:` demanding a **merged** counterpart
is exactly right. For an ADDITION the core half lands **first**: the dependent's adaptation cannot
compile until core's change is pinned. **A gate demanding a merged counterpart here would have
demanded the very deadlock it exists to prevent** — Plugins#1416 could not have merged, by
construction.

So this gate asks for a *statement*, not a *precondition*: name each obliging member and say what
you found. It therefore needs no credential, no API read and no ledger entry, and — like
`Satellite-pins:` — it runs on **fork** pull requests, where the credentialed gates cannot. There is
deliberately **no blanket form**: "nothing implements it" is a claim about a repository this one
cannot see, so it is made per member or not at all. And a reason resting on a live-mesh sweep must
quote the envelope's `searched: true`, for the same #2741 reason `Pairs-with: none` does — in-mesh
C# can implement a core interface and no compiler here can see it.

#### What it costs, measured

| window | additions | of which implementer-obliging |
|---|---|---|
| `main~25 → main` (2026-09-06) | 17 types + 20 members | **3** — #3446's, all of them |
| `main~100 → main` | 49 types + 42 members | **3** — the same three |
| per merge, last 40 first-parent | 15 change the public declaration set | **1 merge meets the gate: #3446** |

So the tax on ordinary work is zero, and the one pull request in a hundred that meets it is the one
that caused the incident.

#### Falsified both ways, against the incident itself

Replaying #3446's own commit (`--base 1bcb764ec^1 --head 1bcb764ec`):

```
ARM 1 — the pull request as it was actually written (no declaration)      exit 1
  [implementer-obliging-added] MeshWeaver.Mesh.Contract :: MeshWeaver.Mesh.IEaGraphAuth.ExchangeAndStore
  [implementer-obliging-added] MeshWeaver.Mesh.Contract :: MeshWeaver.Mesh.IEaGraphAuth.GetAccessToken
  [implementer-obliging-added] MeshWeaver.Mesh.Contract :: MeshWeaver.Mesh.IEaGraphAuth.GetConnection

ARM 2 — the same diff with the three members declared                     exit 0
  Every obliging addition is declared.
```

Exactly the three members `CS0535` named, and nothing else — the three `…Async` forwarders added in
the same diff are default-implemented and are correctly reported as ordinary `member-added`.

🚨 **Arm 2 was RED on its first run, and the reason is worth keeping.** The declaration in a real
pull-request body WRAPS — three qualified member names plus a sentence does not fit on one line — and
the first matcher was single-line, so it read a correct declaration as no declaration at all. A gate
that rejects correct work is how people learn to route around it. The matcher now joins continuation
lines up to a blank line or the next label, and a line that starts `Implementers:` and does not parse
is a **failure**, never an ignored line — the same rule `Pairs-with:` carries, and for the same
reason: an author who believes they declared something and a gate that believes they did not is
indistinguishable from a skip.

#### Four ways to oblige an implementer — two of them are now SEEN (#3489)

Written down because a gate whose limits are inferred rather than stated is how the next incident
gets filed as a surprise. Each row was **run against the detector**, not assumed — the control in the
same run (`public abstract void B();` added to a public abstract class) reports
`implementer-obliging-added` correctly, so a blank was a real blind spot and not a broken probe.

| the change | why an implementer breaks | status |
|---|---|---|
| an interface gains a **base interface** — `interface IFoo : IBar` | every implementer of `IFoo` must now supply `IBar`'s members | ✅ **closed (#3489a)** — `implementer-obliging-base-added` |
| a **`protected abstract`** member is added to a public abstract class | `CS0534` in an external subclass, exactly as for a public one | ✅ **closed (#3489d)** — `implementer-obliging-protected-added` |
| an interface gains an **overload** of a member name it already declares | `void M(string)` beside `void M(int)` is a member every implementer must write | ⛔ **open, deliberately** |
| an existing **default** member is made `abstract` | the body implementers were relying on is gone | ⛔ **out of reach by construction** |

All three closed shapes are **one verdict in three grammars** — an outside implementer must now write
code it did not have to write, and no forwarder on this side can help, because a forwarder rescues a
CALLER. So they share one declaration mechanism (`Implementers:`) and one gate. Splitting them would
ask an author to learn three spellings of one obligation.

**Why the first two were invisible, and why neither is a member diff.** (a) changes **no member of
the interface at all** — `surface_additions` diffs member SETS per type and there is nothing in that
set to differ. (d) changes only members the public index **does not hold by contract** — it is
public-only, so a `protected abstract` member is not an addition, it is not anything.

🚨 **The (d) scan measured ZERO on its first run over a tree that has five, and the zero was
blindness, not absence.** `MEMBER_MODIFIERS` deliberately contains no access keyword but `public`,
so `_leading_modifiers` stops dead at `protected` and answers `set()`. Every downstream test then
reads false and the shape stays invisible — the same *"a sweep's zero has two causes"* shape the
denominator exists to catch, one level further in. The fix strips the access keywords inside the new
scan rather than widening a frozenset the public path also reads: **moving `publicMembersAtBase` to
close a blind spot in a separate index would be the cure breaking the patient.** Verified: on
`origin/main` the report's `publicTypesAtBase`, `publicMembersAtBase`, `publicInterfacesAtBase` and
`implementerObligationsAtBase` are **byte-identical before and after this change** (1 974 / 12 345 /
132 / 452); only the two new counters appeared.

🚨 **And the second zero-discrimination went the other way, which is why both are worth recording.**
`grep -rE '^\s*protected\s+(internal\s+)?abstract'` under `src/` finds **six** declarations; the
scan reports **five**. The missing one is `RoutingServiceBase.RouteImpl`, and the scan is RIGHT:
`RoutingServiceBase` is `internal abstract class`, so nothing outside the assembly can subclass it
and it is correctly out of scope. The crude instrument was the grep. A discrepancy is a question,
not a verdict, and it resolves in whichever direction the evidence points.

**What stays open, and why not closing it is the decision rather than the omission:**

- **The overload shape** is real, and its fix is disproportionate. Member granularity is the NAME, a
  documented choice (#3103) that also makes removing one overload of several silent. Changing it
  would change what "member" means across the whole report — **the removal half included** — and
  recalibrate every floor and every historical count. A gate that fires constantly gets bypassed;
  this one would fire on every overload added anywhere.
- **Making a default member `abstract`** is undetectable by a surface detector by construction:
  nothing is added and nothing is removed. It is shape 7 wearing an interface, and #3276 already
  records why no signature-level instrument can see a behaviour change behind an unchanged
  signature. It stays documented rather than attempted.

Both remain what `Implementers:` cannot ask about, and a reviewer of an interface change should read
them as the two things still to check by hand — down from four.

##### Their own control arms, and one floor that is deliberately 1

`implementerObligationsAtBase` does not constrain either new shape: a parser that found every
interface and every abstract member but stopped reading **base lists** would report a healthy
132 / 452 and zero base edges forever. So each gets its own denominator, published and floored:

| arm | floor | measured on `main`, 2026-09-07 |
|---|---:|---:|
| `interfaceBaseEdgesAtBase` | 5 | **26** |
| `protectedObligationsAtBase` | 1 | **5** |

🚨 The protected floor is **1**, not "far below 5", and that is a decision rather than laziness:
with a true value that small there is no floor that is both meaningful and survivable, so it is set
to catch exactly what a floor *can* catch here — the scan returning nothing at all, which is
precisely how it read before this change. A **partial** regression is caught by a different
instrument: `scripts/check-parser-delta.py` (#3492), which compares two parser versions over one
tree rather than one parser against a guess about the codebase.

**Falsification, three arms, each watched failing:**

| what was disabled | cases that go RED |
|---|---|
| `_base_list` returns `set()` | **3** |
| `_obliges_external_subclass` returns `False` | **1** |
| the `where`-clause guard (constraints read as bases) | **1** |
| the consumer's trigger set narrowed to the #3465 category alone | **2** |

#### The denominator, and what printing it found

`publicTypesAtBase` does not constrain this shape at all: a parser that located every public type
but stopped recognising the word `interface` would publish a healthy 1 900 types and **zero**
obligations forever, and "no obliging additions" would be spelled exactly like "the scan never
looked". So the report carries two more control arms — `publicInterfacesAtBase` and
`implementerObligationsAtBase` — and the gate **refuses** a report where either has collapsed, or
one that predates the shape and carries neither. Measured on `main`, 2026-09-06: **131 public
interfaces, 450 implementer obligations.**

🚨 **Cross-checking that denominator against `grep` found a live blind spot in the EXISTING gate.**
The parser said 130 public interfaces and `grep` said 131. The one it could not see was
`MeshWeaver.Domain.INamed` — and the cause generalises: **a UTF-8 BOM is not a line start, and
`NAMESPACE_RE` anchors on one.** 310 files under `src/` carry a BOM; in 91 of them it sits
immediately before `namespace`, so `^namespace` never matched and the file was indexed with no
namespace in force. Two silent consequences, both now fixed and both fixtured:

| the file's namespace form | what happened | measured |
|---|---|---|
| block-scoped (`namespace N {`) | types are declared at column 4, which without a namespace reads as NESTED — so they were **absent from the index entirely** | **16 public types**, including `ButtonControl`, `HtmlControl`, `SplitterControl`, `NamedAreaControl`, `INamed`. Deleting one triggered no pair gate at all |
| file-scoped (`namespace N;`) | types were indexed under the **wrong key** — `MeshWeaver.Data:IDataStorage` for a type that is `MeshWeaver.Data.IDataStorage` | **128 public types**. A `type-forwards.allow` entry written with the real full name can never match such a key, and a namespace rename is invisible because both sides carry the same wrong one |

`publicTypesAtBase` went from 1 935 to 1 951 on `main` with the one-line fix. **A count nobody
compares against anything is not a control arm** — which is the whole argument for printing the
denominator rather than merely computing it.

##### The same hole had a SECOND victim, and finding it is the reason to sweep siblings

There are three public-surface gates under `scripts/`. Measured against `main`:

| gate | BOM-aware before this change | exposure |
|---|---|---|
| `check-type-forwards.py` | **no** | **LIVE** — 16 public types invisible, 128 miskeyed |
| `check-cross-repo-pair.py` | yes (trivially — it parses JSON and a pull-request body, never C#) | none |
| `check-record-signatures.py` | **no** | **LATENT** — 0 files today |

`check-record-signatures.py` enforces record-signature stability, and its `RECORD_RE` anchors on `^`
and opens with `\s*`. **Python's `\s` does not match U+FEFF** (measured: `re.match(r"\s", "﻿")`
is `None`, `"﻿".isspace()` is `False`), so in a file whose BOM sits immediately before a public
record — a record on **line 1** — the declaration matches nothing and its primary constructor can
change unchallenged. Measured today: **1 281 scanned files, 310 carrying a BOM, 522 public records,
and ZERO where the BOM currently hides one.** The hole is real and currently unexercised. It is
fixed anyway, because *one gate knowing what its neighbour does not is exactly how this survived
unnoticed in both* — and if the two are fixed in different changes they diverge again for however
long that takes.

🚨 **Reading a file without error is not reading it correctly.** Both gates read with
`errors="replace"`, which guarantees **no exception** — a *different* property from correctness, and
an easy one to mistake for robustness. It neither throws nor corrupts; it silently hands back
U+FEFF as the first character. `encoding="utf-8-sig"` would strip it. In the record gate the strip
deliberately lives in `records_in()` rather than at the read site, because the BEFORE side of every
comparison comes from `git show`, not from `read_text` — a fix at the read site would have covered
only half of each diff.

##### The durable protection is the denominator, not the `lstrip`

The `lstrip` fixes today's instance; **a gate that silently indexes fewer types than last time is
indistinguishable from a codebase that shrank.** So each gate now states how much it is seeing and
refuses to certify itself when that collapses:

| gate | denominator | floor | measured on `main` |
|---|---|---|---|
| `check-type-forwards.py` | `publicTypesAtBase` | 500 | 1 951 |
| …the implementer shape | `publicInterfacesAtBase` / `implementerObligationsAtBase` | 50 / 150 | 131 / 450 |
| `check-record-signatures.py` | public records across the whole `src/` tree | 200 | 522 |

The record gate's floor is asserted against the **whole tree in its `--self-test`**, not per run, and
that distinction is forced by its design: it scans only the files a diff CHANGED, so its per-run
count is legitimately zero on most pull requests and could never be a floor. Its self-test runs
first in the `record-signatures` job, so the denominator is checked on every pull request.

Proven in both directions by sabotage, the same way the tenth shape's detector was: removing the
strip reddens the two BOM cases; forcing the floor above the real count reddens the denominator
case; and a `RECORD_RE` that stops matching entirely now reddens a **canary placed first** —
previously it raised `KeyError` from a later case and buried the verdict in a traceback, which is
harder to read than the defect it found.

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
its merged counterpart. Since #3465 one ADDITIVE shape is covered here too — a member added to a
public interface, which breaks IMPLEMENTERS rather than callers and which a pinned dependent
therefore cannot see either (shape 10). Everything else additive or behavioural (shapes 2–5, 7 and
9) is the dependent's to catch when it next builds against the platform — on the event if it tracks,
on the pin bump if it pins.

## Proving it

Every script runs `--self-test` **first** in its job, and every job fails it:

- `check-type-forwards.py --self-test` — 76 cases (29 forwarder verdict + 37 surface report + 10
  transitional allowance). The surface cases prove the report fires on a departure, on a
  **forwarded** move (which the verdict half is correctly silent on), on a whole assembly leaving,
  and — the sixth shape — on #3137's own text in miniature, a renamed method, a member made
  `internal`, a renamed positional record parameter, a removed enum constant, a removed interface
  member and a block-scoped namespace; and stays silent on a within-assembly file move, an internal
  type, an in-mesh doc sample, an addition, a body edit and a removed overload whose name still
  binds. Six more cover the **tenth** shape and are described below; two cover the BOM; and
  **eight cover #3489's two** — a gained base interface, a base *replaced*, a new interface
  obliging nobody, a `where` constraint that is not a base, a whitespace-only generic
  difference, a gained `protected abstract` (with `private protected`, `protected virtual`
  and a non-abstract field alongside it as negatives), a non-abstract class, and an
  `internal abstract` class that is out of scope entirely.
- `check-cross-repo-pair.py --self-test` — 28 cases, including the passing ones. A gate that always
  failed would score identically without them. Five prove the member and sweep rules above.
- `check-package-pin-removal.py --self-test` — the eighth shape, replayed against #3344's own commit.
- `check-interface-addition.py --self-test` — the tenth shape. It fires on #3446's three obliging
  members; stays silent on a diff that adds none and on a declared one; refuses a fenced, commented,
  quoted, reasonless, wrapped-but-unparseable, mis-named or unswept declaration; and **raises**
  rather than reading clean on a starved or shape-less surface report.

**Both directions, mechanically.** Each new mechanism was sabotaged in turn and the self-tests were
re-run, because a case that cannot fail proves nothing:

| what was disabled | surface cases that go RED |
|---|---|
| the BOM strip | 2 |
| the obligation classifier, forced to "never obliges" | 4 |
| …forced to "always obliges" | 8 |
| body detection (every member reads as abstract) | 4 |
| the declaration statement truncated to one line | 3 |

The control arms are `publicTypesAtBase` (the pair gate refuses a base tree declaring fewer than 500
public top-level types; `src/` declares **1 951** across 35 assemblies today) and, for the tenth
shape, `publicInterfacesAtBase` and `implementerObligationsAtBase` (floors 50 and 150; **131** and
**450** today). Every other field of the surface report is legitimately empty on an ordinary pull
request, so without them *"this diff changed nothing"* and *"the scan read nothing"* would produce
the same JSON.

### 🚨 A floor catches "read nothing". It cannot catch "reads LESS than it did" (#3492)

Those three floors are the right instrument for the question they ask, and they were never able to
ask this one. Measured on `origin/main`:

| control arm | floor | measured | slack |
|---|---:|---:|---:|
| `publicTypesAtBase` | 500 | 1 971 | ~1 450 |
| `publicInterfacesAtBase` | 50 | 132 | ~82 |
| `implementerObligationsAtBase` | 150 | 452 | ~302 |

The slack is deliberate — a floor must survive a carve-out wave without going red. It is also
exactly what a UTF-8 BOM walked through. Three bytes before the first character defeat both
`^`-anchored matchers:

```
﻿namespace MeshWeaver.Layout;      NAMESPACE_RE is `^namespace` — no match, so the file's
                                   types are keyed with an EMPTY namespace…
﻿public class ButtonControl        …and TYPE_RE's indent group is `[ \t]*`, which U+FEFF is not
```

`re.match(r"\s", "\ufeff")` is `None` and `"\ufeff".isspace()` is `False`, so neither pattern
forgives it. **310 of 1 271** `.cs` files under `src/` carry a BOM. The damage splits in two, and
the halves fail differently:

- **16 types were in NO index at all.** A *block-scoped* namespace indents its types by four
  columns; with the namespace lost, `expected` falls to `0`, the indent comparison rejects every
  declaration, and `ButtonControl`, `HtmlControl`, `SplitterControl`, `INamed`, `MeshException` and
  eleven others simply were not there.
- **124 more were MISKEYED.** A *file-scoped* namespace leaves its types at column 0, so they were
  indexed — under `Name` instead of `Namespace.Name`. The index also held **123 phantom keys**
  naming nothing real.

That is 0.9 % of the types and 0.8 % of the interfaces: far inside every floor's slack.

**How long, and what it cost — both measured rather than assumed.** The blind set was
**140 types (16 + 124), byte-identically the same set**, at the gate's first commit (`51adbeef3`,
#2689) and 754 commits later. The detector was born blind. Whether anything escaped was then
answered two ways, because one was not enough:

| measurement | result |
|---|---|
| `truth(51adbeef3) − truth(HEAD)` — public types that left `src/` across the window | **0** |
| every `--diff-filter=DR` event on `src/**/*.cs` in the window, re-parsed at its parent | **6 file events, 0 carrying a BOM, 0 blind** |
| every in-place modification (460 file-commits): a BOM'd file losing or renaming a line-1 declaration, or a namespace rename the gate saw identically on both sides | **0** |
| `src/**/*.cs` files that gained or lost a BOM (the one edit that can move a key) | **1**, and it moved nothing — `RegistryUpdateReconciler.cs` line 1 is `using System.Reactive;`, so the BOM had hidden neither a namespace nor a declaration |

🚨 **The endpoint comparison alone would have been wrong to trust**, and that is the part worth
keeping. It cannot see a type born and removed *inside* the window — and that happened: `780beff47`
(#3361) renamed the whole `MeshWeaver.ContainerRegistry` assembly to `MeshWeaver.ContainerImages`,
and the assembly exists at neither endpoint, so seven public types changed namespace invisibly to an
endpoint diff. Same shape as the defect being investigated: *absence of a match read as absence of a
subject.* The per-event pass is what closes it.

**Verdict: zero escapes, and not by luck** — no public type left `src/` at all in the 754 commits
the gate has existed. The gate was blind in 140 places and had not yet been asked the question it
would have answered wrongly. A reprieve, not a defence.

### The control that generalises: a DIFFERENTIAL over a fixed corpus

`scripts/check-parser-delta.py` runs **the merge base's own copy of the detector** and **this
diff's copy** over the **same tree** (the merge base), both in `--surface-json` report mode, and
compares the denominators.

```
detector at the merge base  ─┐
                             ├─→  ONE tree  ─→  two sets of denominators  ─→  delta
detector in this diff       ─┘
```

The corpus is byte-identical for both runs, so **every difference is attributable to the parser and
to nothing else**. The codebase can grow, shrink or be refactored without moving the number: the
control is invariant under code change *by construction*, and moves only when the detector's
behaviour moves — which is its subject. That is what a floor cannot be, because a floor is a
statement about the codebase.

The verdict is deliberately **asymmetric**, because the directions mean opposite things:

| delta | meaning | verdict |
|---|---|---|
| **negative** | the detector sees less of the same tree; every gate downstream now guards a smaller set | **RED**, unless declared |
| **positive** | the detector sees more of the same tree — a fix | printed loudly, **passes** |
| zero | nothing changed | printed with both numbers, so a pass says something |

An intended tightening declares itself in the pull-request body, in the shape `Pairs-with:` already
established:

```
Parser-delta: publicTypesAtBase — nested records were counted as top-level; they are never
independently bindable by simple name, so the old number was wrong upward.
```

The declaration names the **counter and a reason, never the number** — the corpus is the merge base,
which moves while a pull request is open, so a written figure would go stale and train people to
edit it without reading it. It is also **per counter**: declaring `publicTypesAtBase` does not
silence `implementerObligationsAtBase`.

🚨 **No skip-trapdoor.** The step runs unconditionally and asks nothing about its own inputs. A
detector that exits non-zero, or exits 0 and writes no report, is a **failure** naming which side —
never a zero delta. The one path that returns 0 without running anything is a *proof*: when every
subject file is byte-identical between the merge base and the diff, the delta is zero by
construction, and the two SHA-256 digests are printed to show it.

**Both arms, measured on the real repository rather than argued:**

| arm | result |
|---|---|
| `--self-test` | **13 cases, 0 failed** — including a stub reason, a declaration naming the *wrong* counter, a **withdrawn** counter, a counter that stops being an integer, a detector that exits 3, and one that exits 0 writing nothing |
| base `cac18fd9c` (pre-#3487) vs merged `main` | `publicTypesAtBase` **+16**, `publicMembersAtBase` **+111** — #3487's `lstrip`, reproduced as a positive delta over an identical tree, and **passed** |
| base merged `main` vs a working tree with that `lstrip` **reverted** | **RED, exit 1**, on all four counters: `-16` types, `-111` members, `-1` interface, `-1` obligation |

The third row is the one that matters: the control was watched failing on the reintroduced defect,
not merely reasoned about.

**What it deliberately does NOT cover.** `check-record-signatures.py` shares this family's
`^`-anchored parsing and had the same BOM hole (fixed in #3487; exposure was **zero files** at the
time). It is not gated here, for a structural reason rather than an oversight: it scans **the files
this diff changed**, not a whole-tree index, so it publishes no denominator that could shrink. Its
blindness shows up as a missed finding on one file, which a differential over a fixed corpus cannot
see. Giving it a whole-tree index would be a real change to that gate, not a wrapper around it.

### 🚨 A gate added to `main` reaches NEITHER build of a pull request already green (#3508)

The control above landed correct and non-vacuous, and then **did not run on the one pull request
that night which actually changed the parser** (#3503). Not because it was wrong — because of
*where* it was wired. A pull request has two builds, and a gate added afterwards misses both:

1. **Its own `pull_request` run** executes the workflow file **as it stood on the merge ref at the
   time it ran**. A job or step added to `main` later is simply not in that file. Re-running the
   run does not help: it re-runs the same file.
2. **Its `merge_group` run** — the one build that sees the change at the moment it lands — skipped
   the entire `cross-repo-pair` job, whose `if:` is `pull_request` only, for the perfectly good
   reason that its *other* input is the pull-request BODY.

Both exits close on the same pull request, so a gate lands and every pull request already green at
that moment is **permanently exempt from it, silently, and invisibly from the pull request**. On
#3503 the comparison happened to have been run by hand and written into the body — authorship luck,
not a property of the system.

Measured on the queue run for #3546 (`gh-readonly-queue/main/pr-3546-…`, run `34093021863`):

| job | on `merge_group` |
|---|---|
| `Public surface (binary compatibility)` (`record-signatures`) | `completed/success` |
| `Cross-repo pair (public surface)` | `completed/**skipped**` |

**The fix is placement, and only exit (2) can be closed.** Exit (1) is structural: a build executes
the file it was cut from, and nothing changes that. So the parser-delta control moved into
`record-signatures`, the job that already runs on `pull_request` **and** `merge_group` — and which
carries no fork or dependabot clause either, so the move closes those two exits as well.

**The strict/declaring split is written on the EVENT**, the only exemption AGENTS.md sanctions, and
it is the same shape the two gates beside it already use for `--pr`:

| event | form | why |
|---|---|---|
| `pull_request` | `--pr-body-file` supplied | the body is where an intended decrease is declared |
| `merge_group` | **no body — STRICT: any decrease is refused** | a queue entry has no single pull request (its ref can carry several), and nothing should shrink the detector at the moment it lands |

Every build gets exactly one of the two; there is no path on which neither runs, which is the whole
property #3508 is about. **The one thing to know before declaring a decrease:** it is honoured on
the pull request and refused in the queue, so a genuine parser tightening lands only if it does not
shrink a published counter. That is a deliberate trade — the queue is where the change becomes
`main`, and a declaration read there would have to be attributed across a multi-entry group that
carries several pull requests' diffs at once.

**How the move was falsified** (a gate that cannot fail is not a gate — the acceptance test the
issue names is the point of the issue):

| arm | result |
|---|---|
| clean tree, `merge_group` | **exit 0** on the byte-identical proof, both SHA-256 digests printed |
| a planted blindness in the detector's git-tree reader (`MeshWeaver.Layout/` skipped), `merge_group` | **exit 1** — `publicTypesAtBase` `1975 → 1710` (−265), and −1817 / −11 / −46 on the other three |
| the same plant, `pull_request`, empty body | **exit 1**, identical verdict |
| the same plant, `pull_request`, body declaring all four counters | **exit 0** — `↓ DECLARED in the pull-request body`, so the declaring arm is not lost |

Each row executed the step's own `run:` block extracted from `dotnet-test.yml`, not a restatement
of it.

**The sibling shape is #3504**: a repository that hand-rolled a lane opts out of every guard that
lane grows *later*. Same defect one level up, and invisible from the side that is uncovered.

## See also

[Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) ·
[Shared Rule Blocks](/Doc/Architecture/SharedRuleBlocks) ·
[Carving Projects Out Of Core](/Doc/Architecture/CarvingProjectsOutOfCore) ·
[Module Versioning](/Doc/Architecture/ModuleVersioning)
- [Image Pair Skew](/Doc/Architecture/ImagePairSkew) — the same class at the IMAGE level: CD pairs a
  core commit with a Plugins head resolved hours later, and the pair is never executed before it is
  promoted.
