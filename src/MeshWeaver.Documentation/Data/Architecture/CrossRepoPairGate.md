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
here still needs no sibling on disk; `release-packages` still ships without one. That is the same
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

It also covers exactly TWO of the six shapes below — a public type leaving `src/`, and (since #3103)
a public member leaving a type that stays. It does not see the other four, and no detector can:
each of them is detectable only by knowing what the *dependent* consumes.

The structural answer to those is the one #2689 names as its acceptance criterion —
**compile-and-run the dependent's suite against the candidate core commit**, as a CI-time
integration and never as a build-time reference. That keeps the dependency direction intact: core
still builds and ships without the plugin repos present, and the integration is an *observation
about* a candidate commit rather than a *link into* it. This gate is the half of that which needs no
new infrastructure; the other half — `Dependent suites (MeshWeaver.Plugins)` — is described below.

## The six shapes

Collected on #2689 and #3103. Each column says which mechanism sees it.

| # | shape | incident | pair gate | `Dependent suites` |
|---|---|---|---|---|
| 1 | a public **type** leaves `src/` (departure, forwarded move, whole assembly) | #2678 — nine Graph view classes; Plugins' trunk red two hours | **yes** | yes |
| 2 | an **added overload** makes a dependent's `<see cref>` ambiguous (`CS0419`, an error under `-warnaserror`) | #2678 again, a second independent break from one merge | no — nothing is removed | yes — additions dispatch too |
| 3 | a **JSON envelope's field names** change; the dependent parses them by string | #2689 | no | yes, when the dependent's suite exercises the envelope |
| 4 | a **comment** another repo's regex parses as data | #2689 (2026-09-01 carve-out wave, failure #1) | no | only when the dependent's gate runs on the dispatch — its `validate` lane does not |
| 5 | the **i18n mirror**: a *value* change in `strings.{en,de}.json` (#2650) | every Plugins PR red until the mirror PR lands | no | only when `rn-app` runs on the dispatch — it does not (see the receiver's scope) |
| 6 | a public **member** leaves a type that **stays** (field, const, method, property, event, positional record parameter, enum constant, interface member, nested type) | #3137 — `CacheDuration`/`NegativeCacheDuration`; `CS0117`; `Portal hosts (shard 0)` red on every Plugins PR for three hours, *"nothing was tested"* | **yes** (since #3103) | yes |

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

## The dependent's suites run from the core pull request

`.github/workflows/dependent-suites.yml` (the **dispatcher**, one job named
`Dependent suites (MeshWeaver.Plugins)`) and the `core-pr-suites` event in
`MeshWeaver.Plugins/.github/workflows/ci.yml` (the **receiver**).

### The contract

1. **Trigger.** On every same-repo `pull_request` (a fork pull request or dependabot is exempt —
   decided on the *event*, the one place that exemption is allowed) the dispatcher runs the pair
   gate's own detector against the pull request's head vs its merge base. If the public
   declaration set changed — a type or member **removed**, or (with `DISPATCH_ON_ADDITIONS`,
   default `true`) **added** — it dispatches; otherwise it reports *"not dispatched"* and is green.
   A `workflow_dispatch` with a pull request number runs the same path against
   `refs/pull/<n>/merge`, so the wiring is testable on demand.
2. **Dispatch.** An installation token of the org's `meshweaver-cloud` App
   (`DEPENDENT_DISPATCH_APP_ID` / `_PRIVATE_KEY`, the pair notify-dependents already uses), scoped
   to `MeshWeaver.Plugins` with `contents: write`, sends `repository_dispatch` type
   `core-pr-suites` with

   ```json
   {"core_repo": "Systemorph/MeshWeaver", "core_pr": 3170,
    "core_sha": "<the MERGE commit this run executed on — refs/pull/3170/merge>",
    "head_sha": "<the pull request's head>", "core_run_id": "<run id>-<attempt>",
    "core_run_url": "…", "surface": {"removed": 0, "added": 3}}
   ```

   Before sending, the dispatcher proves the token reads the dependent (401/403/404 each get their
   own sentence) and that the dependent's **default branch** declares the receiver — a repository
   with no receiver accepts a dispatch (204) and runs nothing, which would otherwise become forty
   minutes of silence ending in a red that names the wrong cause.
3. **Receive.** The dependent's `platform-ref` job resolves to `core_sha` instead of `main`, and
   **binds the payload to core's history** before anything is built: `core_repo` must be
   `Systemorph/MeshWeaver`, every sha must be 40 hex, and `core_sha` must be the merge commit whose
   second parent is `head_sha` (read from core's public API). A payload naming an unrelated pair is
   refused, not tested. `portal-hosts` then runs exactly as it does for the dependent's own pull
   requests — the moved platform suites compile the dependent's `src/` against THAT core.
   Everything else in that workflow (module bundles, compile-check, the Tests-area gate,
   publish-bake, tag-modules, memex-template, rn-app) carries `github.event.action !=
   'core-pr-suites'`: the publish lanes would otherwise **publish** against a pull request's core,
   and compile-check compiles against the pinned image, so it would spend 45 minutes proving nothing
   about the pull request. The run gets its own concurrency group, so it neither displaces nor is
   displaced by the release lane.
4. **Verdict.** `core-pr-verdict` (`always()`, so a red shard or a refused payload is a verdict
   too) writes a **root commit** whose message is the verdict JSON and points
   `refs/dependent-suites/<core_run_id>` at it — in the **dependent's own repository**, with its
   own `GITHUB_TOKEN`:

   ```json
   {"schema": 1, "dependent": "Systemorph/MeshWeaver.Plugins", "core_repo": "…", "core_pr": 3170,
    "core_sha": "…", "head_sha": "…", "core_run_id": "…-1", "conclusion": "success|failure",
    "portal_hosts": "success", "platform_ref": "success", "platform_sha": "…",
    "run_url": "…", "recorded_at": "2026-09-03T…Z"}
   ```

   Refs older than three days are pruned on the next dispatched run.
5. **Read.** The dispatcher polls that ref every 60 s for `WAIT_MINUTES` (40; the shards take
   25–30), refuses a verdict whose `core_repo`/`core_pr`/`core_sha`/`head_sha`/`core_run_id` are
   not this run's, and finishes with the dependent's conclusion. **No verdict is a red**, never a
   pass.

### Why the verdict travels by a READ

The obvious design has the dependent post a check-run on the core pull request. It was measured and
rejected: the fleet's `meshweaver-cloud` App (id 4220566, installed org-wide) carries
`contents: write`, `metadata: read`, `pull_requests: write` — and **no `checks: write`**
(`GET /orgs/Systemorph/installations`, 2026-09-03). Granting it would hand every dependent's CI the
power to post checks on core, which a compromised dependent could use to green-wash a core pull
request. A marker ref in the dependent's own repository, read by core with `contents: read`, keeps
the direction: **core asks, the dependent answers at home, core reads** — the same class of edge as
`shared-rules` and the pair gate, and it is ledgered with them in
`PlatformNeverDependsOnPluginsGuard.ApiReadLedger`. Marker refs are already this fleet's idiom
(`refs/ci-executed/*` on core).

### Requiring the context

It is not required yet. The cost is real — every dispatch is four portal-host shards, 25–30
minutes each, on the dependent's runners, and 11 of the last 25 merges would have dispatched — so
let it run non-required for a week and read the reds. To require it: Settings → Rules → `main` →
required status checks → `Dependent suites (MeshWeaver.Plugins)`; or, because the job skips on a
fork pull request (a skipped required context reads as satisfied), make it a `needs:` of
`Consolidate test results` with an explicit fail step, the way `cross-repo-pair` is wired. To
narrow the trigger to removals only, set `DISPATCH_ON_ADDITIONS: "false"` in the dispatcher.

### What it does not cover, said plainly

- Only **MeshWeaver.Plugins**, and only its **portal-hosts** shards. The module-bundle lanes also
  compile against `platform-ref` and would add the #2678 AI-bundle shape, but their reusable lane
  publishes on `repository_dispatch` and has no verify-only mode yet; the other satellites build
  against the *published* platform image, not core's source.
- Shapes 4 and 5 (a parsed comment, an i18n value) are exercised by lanes the receiver deliberately
  does not run on this event.
- A pull request that changes **no public declaration** is not dispatched — a JSON envelope renamed
  inside a private class is invisible here too. `workflow_dispatch` runs it anyway.

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
