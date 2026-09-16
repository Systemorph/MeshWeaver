---
Name: Partition Ownership Resolution
Category: Architecture
Description: How a create learns whether its NodeType owns a partition — one resolution per operation, what that shares and what it deliberately does not, and why the nested-instance refusal is not paid for.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5a9 3 0 0 0 18 0a9 3 0 0 0-18 0"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/></svg>
---

# Partition Ownership Resolution

A NodeType declares `ownsPartition: true` and every instance of it is then a **partition root**: a
top-level node whose path is just its id, with its own backing schema, its own `Admin/Partition`
record and its creator as its Admin. Four checks on the create path need that one fact, and
`PartitionOwningTypes.OwnsPartition` is the one answer for all of them — for a type registered in
`src/` and for one declared in mesh content (`Crm/Client`), which is compiled live and is invisible
to the static registry.

## What one resolution costs

| The type is… | The resolution |
|---|---|
| registered in `src/` | the static registry, synchronously, **no read** |
| declared in mesh content | an anchored `namespace:… nodeType:NodeType` listing that establishes the definition EXISTS, then `GetMeshNodeStream(<type>).Take(1)` as System — **two round trips** |

Both halves are required by [CQRS](/Doc/Architecture/CqrsAndContentAccess): a point read of an
absent node answers a routing NotFound that opens the storm-breaker on that path, so existence is
established by the listing first; and the content then comes from the authoritative stream, never
from the index, because this is one known path the create is GATING on.

The answer is a **tri-state**: `true`, `false`, or `null` — "could not be established". `null` is
never folded into `false`: every caller turns it into `PartitionOwningTypes.Undetermined`, a
`NodeRejectionReason.Unavailable` that fails the create CLOSED while saying it reached no verdict.

## The four checks, and the one that keeps its own view

| Check | When | Resolves through |
|---|---|---|
| `RlsNodeValidator.CheckPartitionOwnerCreate` | top-level create only | the operation's shared memo |
| `PartitionWriteGuardValidator` rule 3 | top-level create only | the operation's shared memo |
| `OwnsPartitionProvisioningValidator` | top-level create only | the operation's shared memo |
| `InMeshPartitionOwnerPostCreationHandler` | after the row is written | **its own resolution** |

The first three run back to back, as one `Concat` over one `NodeValidationContext`. They now share
a single resolution through `PartitionOwningTypes.OwnsPartitionOnce`, which memoizes on
`NodeValidationContext.PartitionOwnership` — a `PartitionOwnershipMemo` built fresh per operation,
so it can leak across neither operations nor users, and keyed by node TYPE, so a context copied for
a different node can never read another type's answer. A top-level create of an in-mesh owning type
went from three resolutions (six reads) to one (two).

🚨 **That is a semantic change, not only a saving.** Three independent resolutions could DISAGREE if
the declaration were edited between them, and the disagreement failed the create closed. Sharing one
removes that detection for those three — deliberately. A create should be judged against ONE view of
its type, and the window those three covered is the microseconds between two validators of the same
chain.

🚨 **The window that is actually wide is NOT collapsed.** `InMeshPartitionOwnerPostCreationHandler`
runs on the far side of the storage write, and its job is to re-establish ownership POSITIVELY
before granting the creator Admin on a brand-new partition. It resolves independently, and a
declaration that reads "not owning" — or does not answer — there still faults the create
(`FailsCreateOnError`). Reading the pre-write answer would grant ownership on the strength of a fact
nobody re-established after the row landed.

Fail-closed survives the sharing by construction: the memo stores the tri-state, `null` included, so
a resolution that starved is remembered AS starved and every later check answers `Undetermined`
exactly as it would have on its own. A stored `null` is a recorded decision, never an absent entry.

The memo is a per-operation record, not a cache: nothing in it is keyed by anything an earlier
operation wrote, and it dies with the context — the rule [No Static State](/Doc/Architecture/NoStaticState)
states for everything else that would otherwise outlive its mesh.

## Nested instances of an in-mesh owning type are not refused

`OwnsPartitionProvisioningValidator.Provision` refuses a nested instance of a partition-owning type
— *"A 'Space' owns its partition, so it must be top-level"* — but only for a type the static
registry can see. For a type declared in mesh content the validator returns `Valid()` before it ever
resolves the declaration, so `acme/somewhere/myclient` typed `Crm/Client` is created as an ordinary
child node.

**What that costs today.** The node is not a partition root: `PartitionDefinition.IsPartitionRoot`
is false, so no post-creation handler makes it one, no schema is provisioned, no grant is written,
and the partition-teardown handler will not fire for it on delete. It lands inside a partition the
caller already holds `Create` on, so this is a **data-shape inconsistency** — the type author said
instances of this type own their partition, and here is one that does not — and not a privilege
escalation.

**What closing it would cost, measured.** The cheap option would be for some step already on the
NESTED create path to have materialised the type's definition. Nothing does:

- `NodeTypeResolution.Resolves`, the NodeType-registration check every create runs, probes
  `IStorageAdapter.Exists(nodeType)` and yields a **`bool`**. It establishes that the definition
  exists; it never hands back its content.
- `CreatableTypesCreationValidator` is the one create-path validator that DOES materialise a
  `NodeTypeDefinition` on a nested create — but the **parent's** type, for its
  [CreatableTypes](/Doc/Architecture/CreatableTypes) whitelist, not the created node's own.
- No other validator in the Create chain reads the created node's type definition at all; the ones
  that touch `NodeTypeDefinition` read the node's OWN content, which is a different thing.

So the cheap option collapses into the expensive one: one full resolution — two round trips — on
every nested create of every in-mesh type, fleet-wide, for every non-platform writer.

🚨 **And the cost is not only reads.** The second half is `GetMeshNodeStream(<type>)`, and for a
per-node hub *the read IS the activation*. A cold NodeType activation compiles; the create path's
own control-plane activation budget puts that at 5–45 s in CI, against
`PartitionOwningTypes.ProbeTimeout` of 10 s — and a timeout answers `null`, which every caller turns
into a fail-closed refusal. Paying it on every nested create would put an intermittent refusal,
triggered by whether the type's hub happened to be warm, in front of routine content creation. The
top-level path accepts that risk because a top-level create of an owning type IS a
partition-creation act and is rare; a nested create of a package-declared type is the ordinary
content path.

**The decision.** Not paid for as framed. What would make it cheap is a projection that answers
"does this type own its partition?" without activating the type's hub — the shape
`NodeTypeInstanceLocations` already has for `instanceLocations`, fed by each definition's own hub.
That projection is deliberately **fail-open** (a type whose hub is not live on this process is
simply unknown, and the query fans out in full), which is exactly right for narrowing a query and
exactly wrong for deciding a refusal: enforcement that varies with which hubs happen to be warm is
worse than enforcement that is honestly absent. Closing this properly means giving that lane a
fail-closed denominator first.

## See also

- [Access Control](/Doc/Architecture/AccessControl) — the partition-owning create rule in full
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the resolution is a
  listing plus a stream read, and never a query for content
- [Creatable Types](/Doc/Architecture/CreatableTypes) — the other declaration a create resolves off
  a NodeType definition
