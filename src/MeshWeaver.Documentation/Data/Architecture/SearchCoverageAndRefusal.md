---
Name: Search Coverage and Refusal
Category: Architecture
Description: The MCP search tool answered an unanchored query with a clean count of 0 for nodes it returned the moment the query named a partition — the pre-deploy sweep's zero had a third cause. Why a partitioned store can only ever answer from a subset, why the tool now refuses a query that does not say where to look, what the envelope's coverage field states, and how to read a zero against its denominator.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/><path d="M8 11h6"/></svg>
---

# Search Coverage and Refusal

A mesh query is answered by whichever partitions the store happens to enumerate for it. On a
partitioned backend that is never the whole mesh: the system schemas are excluded from every
fan-out by design, and row-level security drops every partition the caller cannot read before the
SQL is written. An envelope that says `count: 0, truncated: false` on top of such a read is a
partial answer wearing the costume of a total one.

This page records the measurement that made that visible, the mechanism behind it, and the two
changes that follow from it: the `search` tool **refuses** a query that does not say where to look,
and every envelope it does return carries its **coverage**.

## The measurement

memex.systemorph.com, 2026-09-14, one credential, calls seconds apart (MeshWeaver #4274):

| query | `count` |
|---|---|
| `nodeType:LogIncident` | **0**, `truncated: false` |
| `nodeType:LogIncident partitions:all` | **0** |
| `namespace:Admin nodeType:LogIncident` | **0** |
| `namespace:Admin scope:descendants nodeType:LogIncident` | truncated at any limit |
| `nodeType:User` | **0** |
| `path:rbuergi nodeType:User` | 1 |
| `nodeType:Hosting/Deployment` | matches |
| `nodeType:NodeType`, `nodeType:Thread` | matches |

`get @Admin/_LogIncident/0b1877ae6c51353f` returned the node stamped `"nodeType":"LogIncident"`.
The bare filter returned 0 for nodes it itself labels with the filtered type, and `basePath: @Admin`
did not rescue it. (`nodeType:Deployment` also read 0 in the issue; that type is spelled
`Hosting/Deployment`, so that row was a mis-spelled type, not this defect — a positive control worth
keeping because it is the kind of zero that reads the same.)

## Three mechanisms, one symptom

All three are in `PostgreSqlPartitionedMeshQuery` (MeshWeaver.Plugins) and none of them is a bug in
that planner — each is a documented decision whose consequence the `search` envelope did not carry.

1. **An unanchored, unlisted query is served by the fan-out, and the fan-out never sees the system
   schemas.** The planner's `Judge` returns `Refused` for `nodeType:LogIncident`; under the
   production hosts' `UnanchoredQueryPolicy.ServeAndReport` (the 2026-09-03 availability trade,
   documented on that enum) a refused query is *served* by the cross-schema UNION over
   `public.searchable_schemas` and reported at Error in the portal's own log. That registry is built
   by `PostgreSqlCrossSchemaQueryProvider.IsSearchableSchema`, whose `ExcludedSchemas` drops
   `admin`, `auth`, `kernel`, `portal`, `source`, `test`, … by design (`auth` is a mirror that would
   surface every access object twice; `admin` is platform-only). `Admin/_LogIncident` lives in
   `admin`, so the UNION cannot reach it. The Error line names the caller's query; the caller gets
   `count: 0`. Every one of those lines folds into the incident `Admin/_LogIncident/d4c8f6f74ecfa422`
   (#3545), which is how operator searches were re-lighting an issue about a different caller.
2. **A declared fan-out has the same reach.** `partitions:all` turns the verdict into `Served`, and
   the same UNION runs over the same registry — so the declaration silences the log line and changes
   nothing about coverage. To reach a system partition the query must *anchor* to it.
3. **A routing rule can pin a query to a partition the caller cannot read.** `UserNodeType`
   registers `nodeType:User` (no path) → the `Auth` mirror, so the planner serves it from one schema
   — and every UNION branch a signed-in caller gets opens with a `public.partition_access` membership
   test for that schema. Core's `Auth` partition definition (`DefaultPartitionProvider`) declares no
   read policy; the one place the mirror is readable to a signed-in caller is
   `PerSchemaAccessClauseLeakTests`, which seeds a `Public` grant on `auth` by SQL to exercise the
   generator. The middleware the rule exists for reads as System and gets no access clause. On the
   control instance a signed-in platform admin measured `nodeType:User` → 0 (whether the mirror is
   empty there or ungranted cannot be told from outside; both read the same), and `partitions:all`
   did not help because the hint was resolved before the declaration was considered.

And a fourth, on the tool itself: `basePath` became a bare `namespace:{base}`, which is *immediate
children*. `Admin/_LogIncident/…` is two levels down, so the parameter whose documented purpose is
scoping did not reach what it scoped to.

## What changed

### `search` refuses what it cannot cover

`MeshOperations.Search` — the MCP `search` tool and the agents' `Search` — now judges the query
**before any backend sees it**, with the planner's own CI verdict:

```text
served  ⇔  ParsedQuery.IsSufficientlySpecified()  ∨  a routing rule names the partition
```

`IsSufficientlySpecified` is the one definition of "this query says where to look", and it lives on
the query model (`ParsedQuery`, MeshWeaver.Mesh.Contract) so that the planner, the test fixture's
`QueryRouteClassifier` and this entry point cannot drift the way the two executors of the query
language once did (#3511). Four ways to satisfy it, and a query needs exactly one:

| form | example | what it pins |
|---|---|---|
| a concrete anchor | `namespace:Admin scope:descendants …`, `path:Admin/_LogIncident` | one partition |
| a multi-path anchor | `path:Acme/Docs\|Shared/Docs` | the set |
| the explicit request | `… partitions:all` | every searchable partition the caller can read |
| a namespace filter | `namespace:A/Agent\|B/Agent`, `namespace:*/_Thread` | the listed namespaces / the pattern |

Everything else comes back as an `Error:` string that names both remedies. The grace list the
planner also consults (`unanchored-queries.allow`) is deliberately **not** consulted here: it names
the in-repo code callers still carrying known debt, and an operator's search is not one of them — a
graced shape served from this entry point would be exactly the partial answer the refusal exists to
stop. So `search 'nodeType:NodeType'` is refused even though the planner would grace it.

A refusal is an answer. A red is fine. What is not fine is a zero that cannot be told from "none
exist".

### Every envelope carries its coverage

```json
{
  "count": 0, "limit": 200, "truncated": false,
  "coverage": { "scope": "partition", "partitions": ["Admin"] },
  "results": []
}
```

| `coverage.scope` | when | `coverage.partitions` |
|---|---|---|
| `partition` | the query anchors | what the query named — or, when the provider reported, what it actually read |
| `declared` | `partitions:all` | what the provider reported reading; **`null` when it did not** |
| `routed` | a routing rule pinned it | the rule's partition (`["Auth"]`) |

`partitions` is the denominator. The list the storage provider reports on
`QueryResultChange.Partitions` wins, because it is taken *after* every narrowing — the registry's
exclusions and the caller's read grants — so it is the set the rows came from; what the query named
is the fallback; and a declared fan-out on an image whose provider does not report is **explicitly
`null`**. Null is the honest value: an absent or empty list would read as "all" or "none", which is
the same reasoning that leaves `count` out of a `search_chunks` answer under `"searched": false`
(#2741). The aggregator unions the lists of the providers that reported and stays null when none
did (`MeshQuery.MergeProviderObservables`).

### `basePath` reaches the whole subtree

`basePath` now becomes `namespace:{base} scope:descendants` unless the query states its own
`scope:`. "Search from here" means the subtree.

## Reading a zero — how to state the denominator

The pre-deploy NodeType sweep is now written in the declared form, and its zero is stated against
two numbers that the same instrument gives you:

```text
search 'nodeType:NodeType content.compilationStatus:Error partitions:all' limit:200
search 'nodeType:NodeType partitions:all' limit:200
```

> **0 of N over M readable partitions** — N is the second call's `count` (the NodeTypes this
> credential can see at all; a truncated second call means N is a floor, raise the limit), M is
> `coverage.partitions.length` of the first.

When `coverage.partitions` is `null`, the image serving the query does not report what it read;
say "M unknown — coverage not reported by this image" and do **not** pass the sweep on that zero
alone. The system-side census on `/health` (`bake-report`) is the instrument that enumerates as the
process and sees past row-level security — [A Census That Counts Must
Name](/Doc/Architecture/ACensusThatCountsMustName) says what it can and cannot name.

The unanchored form now answers `Error: Query is not sufficiently specified …` rather than 0. If a
sweep you inherit from an older note still reads `search 'nodeType:NodeType
content.compilationStatus:Error'` with no `partitions:all`, the note predates this change; the
refusal names the fix.

🚨 **A tool wearing this refusal must re-house it in its OWN container.** The `Error: …` string is
the surface-wide failure convention of `MeshOperations` and the MCP `search` tool keeps it
(`McpRemoteMeshClient` recognises a refusal by that prefix). But the chat agents' tool answers a
JSON envelope, and its consumers parse it — so handing the prose through threw
`JsonReaderException: 'E' is an invalid start of a value` in every one of them, on the platform's
own release bake: main-cd **#8577** and **#8581** (2026-09-14) each failed the AI module's tests on
it and each skipped the Plugins publication for its platform identity. `MeshPlugin.Search` now
answers `{success: false, refused: <bool>, error: <the operation's text>}` — `success:false` because
the engine's `ThreadExecution.ExtractToolResult` records a JSON object WITHOUT it as a successful
call, and no `count`/`results` because an empty result list would let a consumer read a refusal as
"none exist", which is the misreading this refusal exists to stop. The design record is
`AI/ToolFailureContainer.md` in MeshWeaver.Plugins (Plugins#1852).

Two things the declared sweep still cannot see, and no query form can:

- **A NodeType in a system partition.** The fan-out never enumerates `admin`, `auth`, `kernel`,
  `portal`; a type there is reached only by anchoring (`namespace:Admin scope:descendants
  nodeType:NodeType …`). Measured 2026-09-14 on the control instance: 0 NodeTypes under `Admin`
  for this credential, which is itself a zero with the RLS caveat.
- **A NodeType in a partition you hold no grant on.** That is the denominator M; the census is the
  only instrument that counts past it.

### The cap is the other half of the same rule

A `count` is read against `coverage.partitions`; a `truncated: true` is read against the ORDER the
result was clipped over. The failure has the same shape (MeshWeaver #4950): a `limit:25` listing of
`Hosting/InstanceAction` came back truncated, its newest row six days old, and was read as "no
action for six days" — the rows that explained the outage were there, outside the first 25, in an
order nobody had asked for. A filter-only query now defaults to newest first at every clip site
(`ParsedQuery.EffectiveOrderBy`, [Query Result Scoring](../QueryResultScoring)), so a truncated
page is at least the recent end of the set — but it is still a page, and a reader who needs the
whole set raises the limit until `truncated` reads `false`, exactly as the sweep above raises it
until N stops being a floor.

## What is still open

- **The provider's report is the Plugins half.** `QueryResultChange.Partitions` is the core seam;
  `PostgreSqlPartitionedMeshQuery` populates it with the schema list it actually ran over (after
  the namespace, multi-path, declared-location and partition-access narrowings), and the per-schema
  route reports its one schema. Until that half is on the image, `coverage.partitions` is what the
  query named, and `null` for a declared fan-out.
- **A routing hint overriding an explicit declaration** (mechanism 3) is a planner decision to
  revisit in the same half: a caller that wrote `partitions:all` asked for the fan-out, and the
  `Auth` pin is for the callers that did not say where to look.
- **`nodeType:User` for a signed-in caller** stays 0 through the mirror on a deployment whose
  `auth` partition carries no `Public` read grant (the control instance measured that way). The
  envelope names `Auth` as the partition the zero was read against, and the zero is the mirror
  working as designed: a signed-in user may read a User node they can address by path
  (`UserNodeType`'s access rule admits every authenticated caller on a root User node), but the
  mirror is an enumeration, and enumeration is the disclosure surface #3890 closed. **Code that
  resolves a PERSON from that shape must therefore not issue it as the caller** (#4309). The
  callers in core were the invite-by-email lookups — `SpaceInviteService.GrantOrScheduleAccess`
  (Space invites and Access Control's grant-by-email) and `GroupInviteExtensions.InviteToGroup` —
  which read the zero as "no account" and invited a person who was already here; both now issue
  `SpaceInviteService.AccountLookup(email)`, a request stamped `AsSystem()`, the same
  infrastructure identity `UserIdentityCache.DirectoryQuery` and the developer-login page
  (MeshWeaver.Plugins#712) use. Stamping the REQUEST keeps the identity on the read alone; the
  grant composed on its result still runs as the inviter. The rest of the corpus was already
  system-side (`UserIdentityCache`, `DeviceSeed`, the inbound mail/Teams processors) or is a
  listing that is genuinely the caller's own view (the recipient picker in `SendDocumentLayoutArea`,
  the Access Control subject picker's `AccessSubjectQueries.Users`), where 0 means "not visible to
  you" and the grant-by-email path is the way to reach a person the picker does not show. 🚨 The
  in-memory provider cannot reproduce this zero — it re-decides each row through the type's C#
  access rule, which the mirror's SQL filter never consults — which is why the identical code was
  green on every unit test and is why `InviteResolvesTheInviteeAsSystemTest` pins the lookup's
  viewer as a property of the request through `QueryIdentityResolver`, not as a listing count.

## Tests

- `ParsedQueryAnchoringTest` (MeshWeaver.Hosting.Test) — the predicate and `NamedPartitions` over the
  four specified forms and the two look-alikes (`path:*`, a bare filter).
- `SearchRefusesUnanchoredQueriesTest` (Memex.Portal.Shared.Test) — on a real Monolith mesh: the bare
  form is refused naming both remedies; the same filter anchored and declared is answered, with the
  coverage each states; a routed query names its partition; `basePath` reaches a node two levels
  down, and a caller-stated scope is kept.
- `MeshQueryMergeContractTest.ReportedPartitions_*` — the aggregator unions reported partitions and
  stays null when no provider reported.

Related: [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination) (the
lock-bomb this refusal keeps operator searches out of), [Query Provider
Parity](/Doc/Architecture/QueryProviderParity) (the executor split), [Measuring a Live Portal
Read-Only](/Doc/Architecture/MeasuringALivePortalReadOnly), [Query Syntax](/Doc/DataMesh/QuerySyntax).
