---
Name: Dangling NodeTypes
Category: Architecture
Description: A node can reference a NodeType that resolves to nothing. The two ways it happened, why one is refused with a named bypass and the other is reported rather than blocked, and the repair path both decisions had to leave open — issue #2993.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="M5.5 8.5 9 12"/><path d="M18.5 8.5 15 12"/><circle cx="12" cy="16" r="5"/><path d="m9.5 18.5 5-5"/></svg>
---

# Dangling NodeTypes

An instance can carry a `nodeType` that resolves to **nothing**. It is not an error state you can see:

> A node whose NodeType resolves to nothing has **no per-node hub**. Every read of it waits out
> `NodeTypeEnrichmentHelpers.SlowPathTimeout` and then activates a compilation-error overlay — so
> the node reads as `Unavailable` rather than *failing*, the view renders empty, and a reactive wait
> never completes. Nothing in that picture names the type that is missing.

That is why the live example on production — `rbuergi/_Draft/PartnerRe_EslProposalQA`, carrying
`nodeType: EmailDraft` — sat unexplained. There were **two** ways to get there, and each had a real
counterparty, which is why issue #2993 was filed as a decision rather than fixed as a patch.

## The rule

> **The CREATE and UPDATE boundaries apply the same NodeType existence rule, from one shared
> predicate. The one exemption is named, narrow, logged, and pinned to a single caller. Deleting a
> NodeType is never blocked — it is reported, naming the instances it stranded.**

The predicate is `NodeTypeResolution` (`src/MeshWeaver.Mesh.Contract/Services/NodeTypeResolution.cs`):
a node at the type's **path**, found either as a static node or in persistence.

```csharp
if (string.IsNullOrEmpty(nodeType))                                 return Observable.Return(true);
if (hub.ServiceProvider.FindStaticNode(nodeType) is not null)       return Observable.Return(true);
return persistence is null ? Observable.Return(false)
                           : persistence.Exists(nodeType).Take(1);
```

Not a `TypeRegistry` fact and not a compiled assembly — see
[Import Write Ordering](../ImportWriteOrdering) for why those two are deliberately not conflated.

## Where each write verb stands

| Verb | Reaches | NodeType rule |
|---|---|---|
| `CreateNodeRequest` / `CreateNodesRequest` | `MeshExtensions` create path | Refuses (`InvalidNodeType`) — always has |
| `IMeshService.UpdateNode` (the MCP `update` tool) | `NodeUpdatePipeline` | `DanglingNodeTypeValidator`, refusing a **change** to an unresolvable type |
| `CreateOrUpdateNodeRequest` (import, install, copy, webhook, sync) | `MeshExtensions.ApplyUpdateViaStream` | Same rule, inline — the upsert verb runs no `INodeValidator` at all |
| `patch` | `MeshOperations` | `nodeType` is not in `PatchableFields`; refused outright |
| `GetMeshNodeStream(path).Update(...)` | the owning hub | **Unguarded, deliberately** — see [Residuals](#residuals) |

## Hole A — `update` accepted a NodeType that did not exist

`UpdateAccordingToSourceNode` copied `NodeType` through unvalidated, and no registered
`INodeValidator` produced `InvalidNodeType` for `NodeOperation.Update` — every producer was on the
create path. `ContentDiscriminatorValidator` explicitly returned `Valid()` when the type resolved to
nothing. So `update` was a **supported route to create the orphan condition**.

### The counterparty

`StaticRepoImporter` *relied* on that. Its own comment said so:

```csharp
// A node whose NodeType this pass CANNOT put in place first — carried by no source and
// absent from the mesh, or carried but inside a cycle with it. Only when the node does
// not exist yet: an UPDATE never runs the create path's type check.
bool TypeCannotLand(MeshNode sourceNode, MeshNode? target) => target is null && …
```

`target is null` is the whole point: a node that does **not** exist yet is a reported *blocked
create* (no write attempted — [Import Write Ordering](../ImportWriteOrdering) decision 2); a node
that **already exists** was written anyway, and the update path looking the other way is what let it
land. That covers exactly the cases ordering cannot fix — a cycle where two nodes type each other,
and a type that arrives from another repo.

A blanket refusal would make each of those a per-file **failure**, and `Failed > 0` holds the
caller's git baseline. One cyclic pair would then freeze every *later* commit of the same repo —
which is #2556's non-convergent loop, re-created by the fix for #2993.

### Decision — refuse, with one named bypass

**Refuse on update. Grant `StaticRepoImporter`, and only it, an explicit exemption it has to ask for
by name.** Three properties make that different from leaving the hole open:

1. **It judges a CHANGE, never a state.** An update that keeps the node's current `NodeType` — or
   omits it (`null`, which the merge reads as "keep what state has") — introduces nothing and
   passes. Only a write that *retypes* a node to something unresolvable is refused. (Same carve-out,
   for the same reason, that `ContentDiscriminatorValidator` applies to a round-tripped `$type`.)
   🚨 The "omitted" test is `null`, not null-or-empty, because that is exactly what the merge's
   `sourceNode.NodeType ?? state.NodeType` does: an empty string is a real value the merge writes.
   Clearing a type is still allowed — an untyped node is legal and activates on the mesh default
   chain — but because the resolution rule says so, not because the predicate pretended a clear was
   a no-op.
2. **The bypass is asked for per write, not held open.** The importer sets
   `CreateOrUpdateNodeRequest.AllowUnresolvableNodeType` only when the node **already exists** *and*
   its type is provably unsatisfiable for this pass *and* the write actually changes the type.
   Everywhere else the importer is guarded like every other writer.
3. **It is never silent.** Taking it logs a warning naming the path and the type, and the import
   activity carries a ⚠ line — on every pass, until the type lands.

`UnresolvableNodeTypeBypassGuard` (in `test/MeshWeaver.Documentation.Test`) pins the call-site set:
the declaration, the one reader, the one setter. A new setter fails CI naming the property. A
sanctioned entry that no longer mentions it fails too — a guard whose subject moved while its
expectation did not passes having checked nothing.

> **Why not "warn without refusing"?** Because the warning would be the only thing standing between
> an agent's mistyped `update` and content nobody can read, and the write it warns about is not
> recoverable by the writer: `patch` cannot set `nodeType`, and the node it produced no longer
> answers a point read within a normal budget. A refusal at the boundary is the cheapest place the
> mistake is still cheap.

## Hole B — pruning a NodeType strands its instances

`ComputePrunableNodes` has five guards and none of them is type-aware: a NodeType definition is
pruned exactly like a Markdown page, recursively — source, activity and release history with it.
`PackageInstaller.PruneRemovedNodes` did the same. Nothing checked whether anything still *named*
the type.

### The counterparty

Pruning a retired NodeType is **intended, shipped behaviour** — the What's New entry
*"Retired plugin nodes are pruned on update"* (2026-08-28) says so in as many words, and
[Retiring a NodeType](../RetiringANodeType) documents the opposite failure: a definition the prune
*cannot* reach is a type parked at `compilationStatus: "Error"` that no re-import can clear. So
refusing the prune *outright* would strand the definition instead of its instances — a trade, not a
fix.

### Decision — hold while instances exist, then prune (revised 2026-09-08)

**The first decision (2026-08-28) was "prune, and report": the deletion proceeded and the instances
it stranded were named.** It was revised on 2026-09-08 after it did exactly that on
`memex.systemorph.com`:

```
20:29:14Z [StaticRepoImport] Crm: ⚠ Pruned 1 NodeType(s) that still have instances — those instances
          are now STRANDED … 'Crm/Mail' (1): PartnerRe/Esl/DueDiligenceMail.
```

The Crm repository had retired `Crm/Mail` two days earlier — deliberately, with its one live record
meant to be retyped on the portal *before* the deploy. That portal's Crm sync had been `Skipped`
since 08-30 ([The Import Marker Records Convergence](../ImportMarkerRecordsConvergence)), so the
retirement arrived late and before the retype; the probe found the instance, the report named it,
and the prune ran in the same breath. A client's record was left with no per-node hub, and the bake
gate then refused readiness on the "regression" of a type whose node no longer existed (see
[The bake gate](#the-bake-gate-a-retirement-is-not-a-regression) below). A warning that cannot be
acted on before the deletion protects nothing.

**Now: a NodeType that still has instances is HELD, not pruned.** `NodeTypeInstanceProbe` asks the
same question as before — *are there instances?* — but its answer is a decision:

- The definition stays, together with its own `{Type}/Source/**` and `{Type}/Test/**` (its default
  sources — pruning those while keeping the definition is the orphan shape
  [Retiring a NodeType](../RetiringANodeType) describes). Sources it draws from elsewhere
  (`shared=@Other/Source`) are the repository's to retire and are pruned as directed; the held type
  keeps serving its last usable build.
- The definition is stamped `NodeTypeDefinition.PendingRetirement` — who retired it, when, and which
  instances keep it alive. The stamp is what the bake gate reads.
- The run counts the hold as **preserved**, so it is not converged and the next sync asks the instance
  question again. Once the instances are retyped or deleted, that sync prunes the type exactly as any
  other retired node. **A retirement is a wait for the instances, never a veto**; a type with no
  instances is pruned on the first pass, as it always was.
- In `Additive` mode the held paths stay in the import manifest under their last token, so they
  remain prune candidates — dropping them would file the type as user-added and leak it for ever.
- A probe that **faults** holds too: the deletion is irreversible, the read is not.

The same rule applies to `PackageInstaller.PruneRemovedNodes` (a node-repo package update), which
now reads every candidate, probes, and deletes only what is not held.

Two things already existed and were not wired together, and that remains the whole of the
mechanism:

- **The detector** — `nodeType:{name}`, which lived only as the hidden query behind the Search
  layout area.
- **The policy** — already written down in the `V53_RetypeBuiltinSlideDeckToPublish` migration:
  *"Deleting the built-ins with those rows in place would strip the views from production content,
  so every install must retype first."* [Retiring a NodeType](../RetiringANodeType) states the same
  rule as step 1 of every retirement: *"Establish the instance count is zero."*

That step was a **manual** one, backed by four hand-written per-incident SQL migrations (`V34`,
`V48`, `V52`, `V53`) and zero automation. `NodeTypeInstanceProbe` makes the automated deletion ask
the same question the operator is told to ask:

- One query **per NodeType actually being deleted** — zero for the overwhelming majority of imports.
- Read **as System and mesh-wide**, because instances of a package's type live in user partitions the
  importer's own viewer cannot see. A report that missed them would read as a clean bill of health.
- Delivered four ways: a ⏸ line in the import activity naming the type and the instances, the
  terminal summary, `StaticRepoImportResult.HeldNodeTypePaths` for callers, and — for a GitHub sync
  source — `GitHubSyncConfig.LastSyncNote`, so the settings tab and the status surface say *"in-mesh
  content the repository does not hold"* without anyone reading a log. The activity's terminal
  status is **Warning** until the retirement completes.

### The bake gate: a retirement is not a regression

The second half of the 2026-09-08 incident was downstream of the prune. The readiness sweep
(`DynamicTypePreWarmer`) had enumerated `Crm/Mail` before the sync pruned it; its compile then failed
with the routing's `No node found at 'Crm/Mail'`, was filed as a `CompileError` on a healthy baseline
— a regression — and the pod refused readiness. The recovery watch subscribed to the missing node,
faulted, and stood by its rule *"a watch that cannot observe a recovery must never be read as one"*:
right for an existing node whose read faults, permanent for a node that is gone.

Two classifications close it, both **content verdicts** like `NoSources` (they never gate, and
dependents inherit `UpstreamContentBroken`), filed under `NodeTypeBakeGateState.Retired` and named in
the `/health` payload:

| Status | When | Established by |
|---|---|---|
| `Retired` | the definition carries `PendingRetirement` (held for instances) | the stamp — `ClassifyCompileFailure`, both drivers |
| `Removed` | the definition node no longer exists | a `path:` **listing** that came back and did not name it — never a point read (NotFound + storm-breaker), never the failure text; a listing that faults answers *present* |

And a regression already recorded on a type whose node then disappears is **withdrawn** into the
same bucket (`RetireRegression`), cascading to its derived verdicts exactly like a retraction — it is
not laundered into a recovery. Pinned by `ARetiredNodeTypeIsNotARegressionTest`.

## The repair path both decisions had to leave open

`patch` refuses `nodeType` outright, so a **full-node `update` naming a type that does resolve** is
the only way to fix a mistyped node — and it is exactly what hole A's guard would have closed if it
judged a *state* instead of a *change*. It does not:

| Update | Verdict |
|---|---|
| `Markdown` → `No/Such/Type` | **Refused** — introduces the orphan condition |
| `No/Such/Type` → `No/Such/Type` (content edit) | Allowed — round-trip of what is stored |
| `No/Such/Type` → `Markdown` | Allowed — **this is the repair** |

## Residuals

Named here rather than left for the next reader to rediscover:

- **`GetMeshNodeStream(path).Update(...)` is not guarded.** It is the framework's own mutation API,
  used by the compile pipeline and every watcher; a type check there would sit on the hot path of
  writes that are not retypes at all. The two *upsert* verbs are the boundary every external and
  bulk writer crosses.
- **A node still readable is a precondition of repairing it.** Retyping is allowed, but a node whose
  type is already dangling answers a point read only after the slow-path budget expires. Repair works;
  it is slow, and `NodeUpdatePipeline` says so explicitly rather than reporting "not found" (#2992).
- **A rename does not rewrite instances.** `MoveNodeRequest` on a NodeType leaves every instance
  pointing at the old path — the same stranding as a prune, from a different verb, and not covered by
  the probe.
- **Recognition of a NodeType definition is `ImportWriteOrder.IsNodeTypeDefinition`** — the
  framework's own two-armed test (`Content is NodeTypeDefinition` **or** `NodeType == "NodeType"`),
  reused rather than reimplemented. A definition row that satisfies neither arm is invisible to the
  probe.

## Related

- [Import Write Ordering](../ImportWriteOrdering) — type before instance, the cycle policy, and the blocked-create classification the bypass is the update-side sibling of
- [Retiring a NodeType](../RetiringANodeType) — the manual retirement procedure whose step 1 this automates
- [Static Repo Import](../StaticRepoImport) — sync modes, claims, and what the prune removes
- [NodeType Compilation](../NodeTypeCompilation) — what an unresolvable type does to an activation
- [CQRS and Content Access](../CqrsAndContentAccess) — why the instance probe is a query and the existence check is not
