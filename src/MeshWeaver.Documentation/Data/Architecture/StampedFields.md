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
| A patch of the node hub's data through any reference OTHER than the node itself (a `JsonPointerReference`, an entity or collection reference) | the same delivery step | **refused** on a type that declares stamped fields, naming the field and the reference: its shape cannot be compared field by field, and no platform caller patches a node that way |
| A `DataChangeRequest` creation entry for the node's OWN, existing node (the change pipeline applies it as a replace) | `StampedFieldDataValidator`, judged as an update | **refused** when the resulting content differs in the field — including a removal |
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

- **Per activation:** the node hub reads its type's declaration once, **while it initializes** —
  synchronously for a type registered in code, one durable row read for a type declared in mesh
  content — and keeps it for its life, the same binding the rest of its configuration gets. The read
  is one of the hub's buildup actions, so the hub accepts no message until it is settled, and every
  client write is then judged synchronously on the hub's own turn. 🚨 It must never be read lazily on
  the first write: holding that write back while the row loads and continuing it from the store's
  thread runs it outside the hub's action block, where a later write can overtake it. A read that
  faults is kept as a fault: every write it would judge is refused as `Unavailable` (retryable),
  never let through, and the first refusal starts one re-read — no write waits for it.
- **Per client write:** one cached lookup; a type that declares nothing is not parsed further. Only a
  write to a type that declares stamped fields has its patch inspected.
- **Per create:** one declaration read for a type declared in mesh content (none for a type
  registered in code).

## Consequences to know about

- **Copy, restore and import** of a node whose stamped field is set are client writes like any other:
  a copy (a create carrying the value) is refused, and a restore or import that would change the
  value is refused. Create the copy without the field and let its own hub stamp it; for a restore,
  the type's stamp re-derives the value from the restored node.
- **Retyping clears.** A value a field held while the node's type declared nothing (or declared
  other fields) is not the new type's stamp. When a node is retyped into a type that declares
  stamped fields, those fields are cleared by the node's own hub before an activation bound to the
  new type ever serves it, and only the new type's own code can set them again. Installers that
  retype nodes into newly installed types keep working; they lose any pre-existing value in a
  stamped field, by design. See "Clear on retype" below.
- **Move and copy do not retype.** Both re-create the node at its target with its `nodeType`
  carried verbatim, through `CreateNodeRequest`, so the create half above judges them against the
  same type: a moved or copied node whose stamped field is set is refused, as for any copy.
- **Recycle after a declaration changes.** The declaration is bound when the hub activates (see
  [Stale State Until Recycle](../StaleStateUntilRecycle)); a running activation keeps the declaration
  it read until it is disposed.

## Clear on retype

A node hub is bound to the type its node had when it activated, and judges every client write
against THAT type's declaration. A retype — a client patch, `UpdateNode` or data change that changes
`nodeType` — is such a write: it lands on the activation bound to the OLD type, and is never held
back or refused for the new type's sake (that would need the new type's declaration on the write
path, a row read for a type declared in mesh content).

What follows is the platform's rebind ([`NodeTypeRebindWatcher`](../StaleStateUntilRecycle)): the
retiring activation sees the retype post-commit and recycles itself, so the next access binds the
new type. Before that recycle it SETTLES the retype (`INodeRetypeSettlement`, implemented by
`StampedFieldDeclarationHolder`):

1. It reads the declaration of the type the node carries NOW — the same read a hub start makes, off
   the write path.
2. From then on its guard and data validator judge every client write against the NEW declaration,
   and refuse a further change of `nodeType` as retryable (the retry reaches the new activation,
   which settles that retype in turn).
3. When the new type declares stamped fields, every one that carries a value is removed from the
   content by the hub's OWN write (as System; it never travels as a message, so no guard judges it)
   and flushed durably (`IPostCommitFlush`) — a value written in between, judged against the old
   declaration, is cleared with it.
4. Only then does the watcher post the recycle. The activation bound to the new type reads the node
   back from storage, cleared, and its own code is the only thing that can stamp it again.

A node retyped back to the type its activation was bound to is left alone: every write in between
was judged against that very declaration. A retype into a type that declares nothing clears nothing.
When the read or the clear faults, the activation keeps a faulted declaration and does NOT recycle:
every client write is refused as `Unavailable`, and the first refusal re-runs the settlement, which
recycles the hub itself once it lands — the new type never receives a node its settlement did not
finish.

## The `sync/{id}` route, measured

A `DataChangeRequest` addressed directly to one of the node hub's hosted `sync/{id}` stream hubs
(rather than to the node's own address) is handled there by `SynchronizationStream`'s stream
handler, which this guard does not sit in front of. **It reaches no data.** The sync hub cannot
resolve a workspace of its own (`An exception was thrown while activating MeshWeaver.Data.IWorkspace`),
so every data change sent that way is answered with a `DeliveryFailure`. That includes a change that
leaves the stamped field alone, which is how the test tells "the route applies nothing" apart from
"the guard happened to fire". The framework's own subscribers never send their write-back there:
they address the owner. Test: `DataChange_SentToTheOwnersSyncHub_CannotChangeAStampedField`. If that
handler is ever made to apply changes, it must apply them through the owner, where the guard and
`StampedFieldDataValidator` sit, and that test will fail on the unrelated change first.

## Not established

- **The window between a retype's commit and its settlement is not crash-proof.** The clear is the
  retiring activation's write after the retype has committed. If the process dies, or something
  other than the rebind watcher disposes that activation (an operator's recycle, an installer's own
  `DisposeRequest`), in that window, the next activation binds the new type with the old value — no
  durable record says the retype is unsettled. Measured only through the rebind path; a consumer
  that trusts the field still re-checks (next section).
- **Between a retype's commit and its settlement the forged value is VISIBLE.** The clear runs after
  the retype commits, so a live subscriber of the node (a stream, a change feed) can observe a value
  written under the old type in a field the new type stamps, until the clear lands. What the rule
  guarantees is the state the activation bound to the new type starts from, not every intermediate
  emission — one more reason a consumer re-checks before it acts (next section).
- A write straight to storage that changes `nodeType` (a repair migration, a raw import) passes no
  node hub and therefore no guard at all, retype or otherwise.

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
  declares nothing lands (the negative control); writes posted in order to a fresh node of a type
  declared in mesh content commit in that order; a retype into a stamping type clears a pre-written
  value and the new type's own stamp then lands, a retype into a type that declares nothing leaves
  the value alone (the negative control), and an installer-style retype of many nodes lands and
  clears each. Three more tests cover the paths around those. After a recycle, the node read back
  from its durable row still refuses a change of the stamp. A type declared in mesh content that
  gains `stampedFields` while an instance runs is enforced once that instance is recycled, with the
  declaration read from the stored type row. A data change sent to a `sync/{id}` hub reaches no data.

## Related

- [Access Control](../AccessControl) — who may write a node at all; this rule narrows it per field.
- [Authorize as Caller, Execute as System](../AuthorizeAsCallerExecuteAsSystem)
