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

#### 🚨 Four ways to oblige an implementer that this gate does NOT see

Written down because a gate whose limits are inferred rather than stated is how the next incident
gets filed as a surprise. Each row was **run against the detector**, not assumed — the control in the
same run (`public abstract void B();` added to a public abstract class) reports
`implementer-obliging-added` correctly, so a blank here is a real blind spot and not a broken probe:

| the change | why an implementer breaks | why the detector is blind |
|---|---|---|
| an interface gains a **base interface** — `interface IFoo : IBar` | every implementer of `IFoo` must now supply `IBar`'s members | the detector diffs member SETS per type; it does not diff base lists |
| an interface gains an **overload** of a member name it already declares | `void M(string)` beside `void M(int)` is a member every implementer must write | member granularity is the NAME, deliberately — the same limit that makes removing one overload of several silent |
| an existing **default** member is made `abstract` | the body implementers were relying on is gone | nothing is added or removed; this is shape 7 wearing an interface |
| a **`protected abstract`** member is added to a public abstract class | `CS0534` in an external subclass, exactly as for a public one | only `public` members are indexed, so it is not an addition at all |

The first two are cheap to close and the second would change what "member" means across the whole
report, including the removal half; the third is undetectable by any surface detector, as shape 7
already records. Until then they are what `Implementers:` cannot ask about, and a reviewer of an
interface change should read them as the list of things still to check by hand.

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

- `check-type-forwards.py --self-test` — 68 cases (29 forwarder verdict + 29 surface report + 10
  transitional allowance). The surface cases prove the report fires on a departure, on a
  **forwarded** move (which the verdict half is correctly silent on), on a whole assembly leaving,
  and — the sixth shape — on #3137's own text in miniature, a renamed method, a member made
  `internal`, a renamed positional record parameter, a removed enum constant, a removed interface
  member and a block-scoped namespace; and stays silent on a within-assembly file move, an internal
  type, an in-mesh doc sample, an addition, a body edit and a removed overload whose name still
  binds. Six more cover the **tenth** shape and are described below; two cover the BOM.
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

## See also

[Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) ·
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) ·
[Shared Rule Blocks](/Doc/Architecture/SharedRuleBlocks) ·
[Carving Projects Out Of Core](/Doc/Architecture/CarvingProjectsOutOfCore) ·
[Module Versioning](/Doc/Architecture/ModuleVersioning)
- [Image Pair Skew](/Doc/Architecture/ImagePairSkew) — the same class at the IMAGE level: CD pairs a
  core commit with a Plugins head resolved hours later, and the pair is never executed before it is
  promoted.
