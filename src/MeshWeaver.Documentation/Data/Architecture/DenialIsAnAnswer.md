---
Name: A Denial Is an Answer
Category: Architecture
Description: A permission check on a hub that carries no evaluator grants Permission.All — which is how every session surface (MCP, REST, gRPC, CLI) shipped a pre-flight that could not fail. Plus the rule the crash on the other side taught - a refusal the mesh decided is rendered as the operation's answer, never raised as a fault.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/><path d="M4.5 4.5 19 19"/></svg>
---

# A Denial Is an Answer

Two rules, one incident ([#3121](https://github.com/Systemorph/MeshWeaver/issues/3121)).

🚨 **A hub's configuration inherits NOTHING, and a permission check on a hub with no evaluator
answers `Permission.All`.** So a gate written on the wrong hub is not a weak gate — it is a gate
that cannot fail.

🚨 **A refusal the mesh DECIDED is an answer the operation renders.** Deciding is the operation
succeeding at its job. Raising it as an exception tells the caller the tool is broken.

## The incident

An MCP caller without write access called `recycle` on `Store/Catalog`. The mesh refused —
correctly — and the client got this:

```
fail: ModelContextProtocol.Server.McpServer[1433779783]
      "recycle" threw an unhandled exception.
      System.UnauthorizedAccessException: Access denied: user 'rbuergi' lacks Update permission on 'Store/Catalog'
         at MeshWeaver.Mcp.McpToolResult.AsToolResult[T](…)
```

`MeshOperations.Recycle` has carried an actionable refusal envelope since #2901 — *"Recycle
requires Update permission on the target node … ask someone with write access"* — gated on
`hub.CheckPermissionOutcome(path, Update)`. It never ran. Not on this call, and not on any call
through any session surface, since the day it was written.

## Why the pre-flight could not fail

`HubPermissionExtensions.ResolveEvaluator` is three lines:

```csharp
private static EffectivePermissionsDelegate ResolveEvaluator(IMessageHub hub) =>
    hub.Configuration.Get<EffectivePermissionsDelegate>()
    ?? MessageHubPermissionExtensions.DefaultEvaluator;   // (_, _, _) => Observable.Return(Permission.All)
```

It does not walk the parent chain — and `MessageHubExtensions.CreateMessageHub` builds a **fresh**
`MessageHubConfiguration` for every hub, inheriting nothing. `AddRowLevelSecurity()` installs the
evaluator on exactly two places: the **mesh hub** and every **per-node hub**. A session hub is
neither.

`SessionHubFactory` is the one factory behind every API surface — MCP, REST, gRPC, the CLI, and the
headless local sidecar all issue their operations on a `portal/{prefix}-{session}-{instance}` hub it
materialises. It never copied the evaluator. So every client-side check those surfaces issued
answered *granted*, for every caller, on every path.

Measured on a real mesh, a `Viewer` (`Read|Execute|Api`) asking the SAME question two ways:

| Asked on | `Update` on a node the Viewer may only read |
|---|---|
| the session hub (`portal/mcp-…`) | **granted** |
| the mesh hub | denied |

**This was not an authorization hole by itself.** The owner's `AccessControlPipeline` was, and
remains, the authority on every write, and it fails closed. What the inert pre-flight cost was
*legibility* — and, where an operation's authorization rode on a write that turned out to be a
no-op, rather more than that.

### The three things it actually cost

Measured before and after, same mesh, same Viewer, through a real session hub:

| Operation | Before | After |
|---|---|---|
| `recycle` a **NodeType** | `THREW UnauthorizedAccessException` — the reported bug | the refusal envelope |
| `recycle` a **plain node** | `{"status":"Recycled"}` — **the `DisposeRequest` went out** | the refusal envelope |
| `create` | `Created: …` — **the node was written** | `Access denied: Create permission required` |
| `export` | every node in the subtree | only nodes the caller may `Export` |

The middle two are the interesting ones.

**`recycle` on a plain node.** Recycle is two halves of one operation: stamp a release request, then
dispose the hub. The [2026-08-30 fix](/Doc/WhatsNew/2026-08-30-a-refused-recycle-no-longer-tears-the-hub-down)
made a refused stamp refuse the whole recycle. But the stamp only writes anything on a **NodeType**
node — on anything else the update is the identity function, so nothing was posted, nothing was
gated, and the destructive half ran for free. Authorization that is a *side effect* of a write
disappears exactly when the write does.

**`export`.** `MeshOperations.Export` filters the subtree per node against the caller's `Export`
permission, and its own comment said the check "runs on the mesh hub". It did not: it resolves
`hub.ServiceProvider.GetRequiredService<IMessageHub>()`, which on a session surface hands back the
**session** hub. The one thing standing between an MCP `export` and a subtree the caller may not
read answered *granted* for every node.

## The fix, and why it is two halves

**Half one — `SessionHubFactory` copies the mesh hub's evaluator into every session hub.** The
pre-flight becomes a real gate, so a refusal is the normal, legible path and the destructive half
never runs. `MeshExtensions.NodeOperationExecutionHub` had already been fixed this way, for exactly
this reason, and its comment spells the hazard out; the session hub simply never got the same
treatment.

Nothing is widened by this: the owner already refused everything the pre-flight now refuses. A mesh
deliberately built **without** RLS stays ungated, because there is then no delegate to copy.

**Half two — the owner's verdict is rendered, not raised.** A pre-flight is check-then-act. It runs
on a different hub, at an earlier moment, against a permission fold that can have moved on: an
assignment revoked in between, a cross-silo fold, an identity lost across a scheduler hop. The owner
stays the authority, and when it refuses, `RecycleCore` now answers in the operation's own envelope
— the same sentence the pre-flight denial uses, because the caller should not be able to tell which
evaluator refused them, nor be told two different things about one fact.

## 🚨 A verdict and a non-verdict are not the same answer

Half two is where this change could have made things worse. "You lack permission" is actionable and
final; "I could not evaluate access" is transient and fail-closed. Collapsing the second into the
first sends a correctly-entitled caller to request rights they already hold — the lie [#974](https://github.com/Systemorph/MeshWeaver/issues/974)
removed from `AccessControlPipeline` and #2901 removed from `MeshOperations`' own pre-flight.

`MeshOperations.IsWriteDenial` is the seam, and it decides on the **typed** failure — never by
matching a message, which drifts the moment someone rewords a banner:

| Fault | Denial? | Because |
|---|---|---|
| `UnauthorizedAccessException` | **yes** | the write path mints it for one thing only: `DeliveryFailure{Unauthorized}`, which the pipeline posts only after a decisive refusal |
| `DeliveryFailureException{Unauthorized}` | **yes** | the same verdict, before `UpdateRemote` maps it |
| `DeliveryFailureException{ShuttingDown}` | no | the activation is going away — [re-probe](/Doc/Architecture/RidingOutAShuttingDownAddress), do not go asking for rights |
| `DeliveryFailureException{Unavailable}` | no | the fold reached no verdict (#974) |
| `TimeoutException`, anything else | no | **the default must never accuse** |

That the tri-state survives the hop *as a type distinction* is not an accident:
`AccessControlPipeline` projects it onto the bus vocabulary exactly once, and
`MeshNodeStreamHandle.UpdateRemote` keeps the split by raising `MeshNodeStreamException` — not
`UnauthorizedAccessException` — for the other two. This is the write-side twin of
`NodeReadOutcome.FromReadFailure`, which does the same job for reads.

## Writing a new operation

- **Ask the permission question on a hub that can answer it.** If you are writing a
  `CheckPermission` / `CheckPermissionOutcome` call, know which hub it lands on. A session hub now
  answers correctly; a hub you build yourself does not, unless you copy the evaluator.
- **Never let authorization be a side effect of a write.** Decide, then act. A stamp that happens to
  be gated is not a gate — it stops being one the day the stamp becomes a no-op.
- **A refusal is a return value.** Render it in the operation's envelope. The tool transport's
  `IObservable` → `Task` bridge is not an error handler, and an exception there reaches the client
  as "the tool is broken".
- **Never widen a non-verdict into a denial.** Route it through `IsWriteDenial`, or say honestly
  that you could not find out.

## Not localised, deliberately

The refusal sentences on this surface are JSON envelope fields on the **machine** tool surface (MCP,
REST, CLI), consumed by an agent runtime that reasons about them and renders its own text to the
human. They sit on the model side of the line [Localization](../Localization) draws around
tool-facing text — the same side as an LLM tool parameter's `[Description]` — and localising them
would make the answer depend on whichever locale happened to be attached to a session. The
human-facing surfaces render their own refusal — the portal's typed error card, via
`AreaErrorClassifier` — and that one **is** localised.

## Related

- [Access Control](../AccessControl) — the permission model these gates evaluate.
- [AccessContext Propagation](../AccessContextPropagation) — how the caller's identity reaches the
  gate in the first place; a lost context is the other way a pre-flight and an owner disagree.
- [An Unreachable Store Is Not a Refusal](../StoreUnreachableIsNotARefusal) — the same
  verdict/non-verdict rule, on the storage side.
- [Unanchored Security Reads](../UnanchoredSecurityReads) — why "no result" and "not allowed" are
  the same value inside the fold, and what that forbids.
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — what an unhandled fault costs
  downstream of a tool surface.
