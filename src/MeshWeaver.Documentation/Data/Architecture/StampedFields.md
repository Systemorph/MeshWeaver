---
Name: Stamped Fields
Category: Architecture
Description: A NodeType declares content fields only its instances' own hub may write; the platform refuses every client patch, data change and create that would set or change one, naming the field.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/><circle cx="12" cy="16" r="1"/></svg>
---

# Stamped Fields

Some values on a node are not data a person enters but facts the node type's own code records —
who confirmed an eval case, who approved a request — and something downstream **trusts** them as an
identity. A nightly eval runner, for example, starts each case's conversations as the person in its
`confirmedBy` field. The type's own hub can stamp that field correctly, but as long as anyone holding
Update on the node may patch the stored value afterwards, the stamp proves nothing.

So the platform has a rule for it: **a stamped field is written by the node's own hub and by nothing
else.**

## Declaring one

A NodeType lists the fields in its definition — the JSON property names inside `content`:

```json
"content": {
  "$type": "NodeTypeDefinition",
  "configuration": "config => config.WithContentType<EvalCaseContent>().StampConfirmationOnActivation()",
  "stampedFields": ["confirmedBy"]
}
```

`NodeTypeDefinition.StampedFields` is declared, not derived: it is read from the type's static
registration or its durable NodeType row, the same row that names its sources and configuration. It
says nothing about how the value is computed — that stays the type's own code, typically a watcher
on its own node that writes the field through `GetMeshNodeStream().Update(...)`.

## What the platform enforces

| Write | Where it is judged | Outcome when it touches a stamped field |
|---|---|---|
| A cross-hub patch — `GetMeshNodeStream(path).Update(...)` from any other hub, MCP `update` / `patch` / `edit_content`, the update branch of an upsert, a restore, a GitSync import | a delivery step on the node's own hub, before the merge runs | **refused** when the patch would CHANGE the field (a different value, a removal, a splice); naming it re-asserting the live value is a no-op and passes |
| A data change from another address (a synced subscriber's write-back, a direct data change) updating the node | `StampedFieldDataValidator`, a validator in the data layer's change pipeline on the same hub | **refused** (a failed `DataChangeResponse` naming the field) when the resulting content differs in the field |
| A create (`CreateNodeRequest`, an upsert's create branch, a `DataChangeRequest` creation) | `StampedFieldCreateValidator`, a create validator | **refused** when the content carries a non-empty value for the field |
| The node's own hub writing its own node (`GetMeshNodeStream().Update(...)` on its own workspace) | — | allowed: this is the only path that sets the field |
| Any read | — | unchanged |

A refused patch is a `DeliveryFailure` with `ErrorType.Unauthorized` whose message names the field, the
node and the type, for example:

> Access denied: the write to 'confirmedBy' on 'Feedback/_Evals/agent/case-1' was refused —
> 'confirmedBy' is a stamped field of Feedback/EvalCase, written only by the node's own hub, never by
> a client patch. Nothing was changed; leave the field out of the write.

The whole write is refused, never stripped of the field: a silent strip would tell the writer its
value landed. A write that leaves the field alone is unaffected — the check compares the field and
nothing else.

## Why the owning hub is exact, and the sender is the test

A per-node hub's own writes never travel as a message: `GetMeshNodeStream()` on its own node commits
directly on its data source (`MeshNodeStreamHandle.UpdateOwn`). Every write from anywhere else — a
portal, an agent, another node's hub, an MCP call, even code running as System on another hub —
arrives at the node's hub as a `PatchDataRequest` or a `DataChangeRequest` whose sender is a
different address. So "came from another address" is exactly "not the node's own code", without
any identity, role or impersonation having to be trusted. System is not exempt: platform code that
runs elsewhere is still a client of the node.

## What it costs

- **Per activation:** the node hub reads its type's declaration once — synchronously for a type
  registered in code, one durable row read for a type declared in mesh content — and keeps it for its
  life, the same binding the rest of its configuration gets. A read that faults is not cached; the
  write it was judging is refused as `Unavailable` (retryable), never let through.
- **Per client write:** one cached lookup; a type that declares nothing is not parsed further. Only a
  write to a type that declares stamped fields has its patch inspected.
- **Per create:** one declaration read for a type declared in mesh content (none for a type
  registered in code).

## Consequences to know about

- **Copy, restore and import** of a node whose stamped field is set are client writes like any other:
  a copy (a create carrying the value) is refused, and a restore or import that would change the
  value is refused. Create the copy without the field and let its own hub stamp it; for a restore,
  the type's stamp re-derives the value from the restored node.
- **Recycle after a declaration changes.** The declaration is bound when the hub activates (see
  [Stale State Until Recycle](../StaleStateUntilRecycle)); a running activation keeps the declaration
  it read until it is disposed.

## Not established

- A `DataChangeRequest` addressed directly to one of the node hub's hosted `sync/{id}` stream hubs
  (rather than to the node's own address) is handled there by `SynchronizationStream`'s stream
  handler, which this guard does not sit in front of. Whether that handler reaches the node's own
  data source was not measured when the rule shipped; the framework's own subscribers never send
  their write-back there (they address the owner).

## Trusting a stamped field

The rule guarantees who WROTE the field, not that the value is still a valid authority. A consumer
that acts on it still re-checks at the time it acts — the
[authorize as the caller, execute as System](../AuthorizeAsCallerExecuteAsSystem) pattern: for the
nightly runner, that the stamped confirmer still holds Update on the case immediately before the run.

## Where it lives

- `NodeTypeDefinition.StampedFields` — the declaration.
- `MeshWeaver.Graph.Security.StampedFields` — the resolver, the change detection, and the node hub's
  delivery step (`AddStampedFieldGuard`, installed with every mesh data source).
- `MeshWeaver.Graph.Security.StampedFieldDataValidator` — the data-change half: the data layer runs
  its validators only for a change that ARRIVES as a message, so the owner's own commits never meet
  it. It shares the hub's declaration (`StampedFieldDeclarationHolder`, a singleton of the node hub's
  own container) with the delivery step.
- `MeshWeaver.Graph.Security.StampedFieldCreateValidator` — the create half.
- Tests: `test/MeshWeaver.Graph.Test/StampedFieldsTest.cs` — a client patch, an `UpdateNode` and a
  data change that change the field are refused naming it, the owner's stamp lands, an unrelated field on the same
  node still lands, a create with a forged value is refused, and the same write on a type that
  declares nothing lands (the negative control).

## Related

- [Access Control](../AccessControl) — who may write a node at all; this rule narrows it per field.
- [Authorize as Caller, Execute as System](../AuthorizeAsCallerExecuteAsSystem)
