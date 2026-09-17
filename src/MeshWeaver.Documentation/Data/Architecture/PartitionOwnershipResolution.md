---
Name: Partition Ownership Resolution
Category: Architecture
Description: How a create learns whether its NodeType owns a partition — one resolution per operation on the top-level path, what that shares and what it deliberately does not, and how a nested instance is refused from the definition's durable row without activating the type's hub.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5a9 3 0 0 0 18 0a9 3 0 0 0-18 0"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/></svg>
---

# Partition Ownership Resolution

A NodeType declares `ownsPartition: true` and every instance of it is then a **partition root**: a
top-level node whose path is just its id, with its own backing schema, its own `Admin/Partition`
record and its creator as its Admin. Four checks on the TOP-LEVEL create path need that one fact, and
`PartitionOwningTypes.OwnsPartition` is the one RESOLVER all four use — for a type registered in
`src/` and for one declared in mesh content (`Crm/Client`), which is compiled live and is invisible
to the static registry. Three of the four also share one ANSWER; the fourth resolves again after the
write, on purpose. A NESTED create needs the same fact for the opposite reason — to refuse an owning
type below the root — and answers it through a second resolver that never activates the type's hub.
Which is which, and why, is the rest of this page.

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
never folded into `false`, and every caller fails CLOSED on it — but by two different mechanisms,
because they sit on two different sides of the write:

- the three **validators** return `PartitionOwningTypes.Undetermined`, a
  `NodeRejectionReason.Unavailable` that refuses the create while saying it reached no verdict. The
  row is never written.
- the **post-creation handler** runs after the row exists, so there is no verdict left to return: it
  FAULTS the create (`access.partitionCreate.ownerUnestablished`) and, being `FailsCreateOnError`,
  the create is reported as failed and the row compensated. `false` takes the same path there — at
  that point a type that does not own a partition contradicts the decision that let the root be
  written, so it is a fault and not a quiet skip.

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
a different node can never read another type's answer. The PRE-WRITE phase of a top-level create of
an in-mesh owning type went from three resolutions (six reads) to one (two); the whole create still
costs two resolutions — four reads — because the post-creation handler deliberately makes its own.

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

## Nested instances of an in-mesh owning type are refused — from the definition's ROW

`OwnsPartitionProvisioningValidator` refuses a nested instance of a partition-owning type: an owning
instance IS a partition root, so its path is just its id. Until #4449 item 1 it did so only for a
type the static registry can see. For a type declared in mesh content it returned `Valid()` before
resolving anything, so `acme/somewhere/myclient` typed `Crm/Client` was created as an ordinary child
node — not a privilege escalation (the node landed inside a partition the caller already held
`Create` on, `PartitionDefinition.IsPartitionRoot` was false, so no schema, no grant and no teardown
followed it) but a **data-shape inconsistency**: the type author said instances own their partition,
and here was one that did not. It is now refused for both, with the same keyed sentence
(`access.partitionCreate.nestedOwningType`) in the caller's language and the same
`NodeRejectionReason.InvalidPath` — which the importer already classifies as a content VERDICT, so a
repository carrying such a node is not re-imported at the same fingerprint.

What makes this hard is not the refusal. It is answering "does this type own its partition?" on the
ORDINARY content path — every nested create of every non-static type — without putting an
intermittent refusal in front of it.

### Why the top-level resolution cannot be reused

Nothing already on the nested path materialises the created node's own definition:

- `NodeTypeResolution.Resolves`, the NodeType-registration check every create runs, probes
  `IStorageAdapter.Exists(nodeType)` and yields a **`bool`**.
- `CreatableTypesCreationValidator` materialises a `NodeTypeDefinition` on a nested create — but the
  **parent's**, for its [CreatableTypes](/Doc/Architecture/CreatableTypes) whitelist.

So the question needs a read of its own, and the top-level resolver's read is the wrong one. Its
second half is `GetMeshNodeStream(<type>)`, and for a per-node hub *the read IS the activation*. A
cold NodeType activation compiles; the create path's control-plane activation budget puts that at
5–45 s in CI, against `PartitionOwningTypes.ProbeTimeout` of 10 s, and a timeout answers `null`,
which fails closed. The top-level path accepts that because a top-level create of an owning type IS a
partition-creation act and is rare. On the nested path it would refuse routine content creation
whenever the type's hub happened to be cold.

### Why a hub-fed projection cannot be the answer either

`NodeTypeInstanceLocations` answers `instanceLocations` with no read: each definition's OWN hub
publishes its declaration into a mesh singleton while it is live on this process. Copying that shape
for `ownsPartition` fails on the DENOMINATOR, whatever the unknown case is made to do. Its denominator
is "the types whose hubs are warm on this process" — a type is unknown whenever its hub is cold, lives
on another silo, or was recycled — and each of the three possible answers for unknown is wrong for a
refusal:

| Unknown answers… | Consequence |
|---|---|
| allow | enforcement varies with which hubs are warm — worse than enforcement that is honestly absent |
| refuse | ordinary content is refused whenever a type's hub is cold — the intermittent refusal above |
| activate, then answer | the activation cost above, paid per create |

Completing the denominator by warming every owning type's hub up front pays the activation per
process instead of per create, and still misses a type installed after the warm-up. That projection
is **fail-open** by design, which is exactly right for narrowing a query (unknown fans out — slow,
never partial) and exactly wrong for deciding a refusal.

### The resolution: the durable row, whose denominator is complete by construction

A nested create resolves ownership through `PartitionOwningTypes.OwnsPartitionWithoutActivating`,
from exactly the two sources the create's own existence rule consults — and in the same order:

| The type is… | The resolution |
|---|---|
| registered in `src/` | the static registry, synchronously, **no read** |
| anything else | `IStorageAdapter.ReadMany([<type>])` — ONE read of the definition's durable row. The type's own hub is never addressed, so the NodeType is never compiled to answer this; no index is consulted |

🚨 **Two things about that read are worth stating exactly, because both were review findings and
both are about what the seam really does.**

**It is `ReadMany`, not `Read`, because `Read` can WRITE.** Where partition storage hubs are
configured `IStorageAdapter` is `RoutingProxyAdapter`, and its `Read` is wrapped in
`LegacyUserPartitionRepair`: for a bare, partition-root-shaped path an absent row triggers a
legacy-twin probe and can durably write a repaired root. An ownership CHECK may not write anything.
Both that proxy and `PersistenceService` override `ReadMany` precisely to route around the repair
("the repair is for a bare partition-ROOT point read"), so it asks the store the same question with
no side effect and answers a path the store does not hold by simply omitting it.

**"Without activating" is a claim about the TYPE's hub, not about locality.** In that same
configuration the store is reached by a routed request to the PARTITION's storage hub, which may
itself be cold. That is the hub `NodeTypeResolution.Resolves` asks moments later, through the same
adapter, for the very same create — so this check adds no activation and no availability dependency
the create did not already have. What it never touches is the type's own per-node hub, whose
activation compiles the NodeType; that is the 5–45 s cost, and it is the one this design exists to
avoid.

**Why nothing is "not known on this process".** The set of types a create can proceed with at all is
*static ∪ stored*: a type in neither is refused as `NodeType '…' is not registered` by
`NodeTypeResolution.Resolves`, which runs right after the validators and reads the same store. The
resolver reads exactly that set, so every type the create could land with has a row this read
reaches — from every process, whichever hubs are warm, cold or on another silo. The denominator is
the store, not the process.

**The three answers, and what a nested create does with each:**

| Answer | When | The nested create |
|---|---|---|
| owns | the static or the stored definition declares `ownsPartition: true` | **refused** — `InvalidPath`, `access.partitionCreate.nestedOwningType` |
| does not own | the declaration says `false`; the row is not a NodeType definition; the row is ABSENT (the store's verdict, not a missing answer); the host has no storage adapter | **proceeds** |
| could not be established | the store faulted, or did not answer within `ProbeTimeout` | **refused** — `Unavailable`, `access.partitionCreate.undetermined`: retryable, not a verdict |

🚨 **An absent row answers "does not own", not "unknown", on purpose.** Absent is a verdict the store
gave, and the create does not land on it: the existence check that follows refuses the type as
unregistered, which is the TRUE reason. Answering unknown would replace that message with "could not
be established" for every mistyped type. The one window where it matters is a definition created
between this read and that probe — the same microseconds-wide shape as a declaration edited mid-create,
and it yields exactly the pre-change behaviour for that one create.

🚨 **Measured while proving it: the top-level resolver is not even fail-closed here.** With the
store made to fault the definition's row read, the listing half of `OwnsPartition` — served by the
in-memory query provider, which drops a row whose read faults so the rest of a listing survives —
answered "absent", i.e. "does not own", and a nested instance wired to that resolver was CREATED.
On the nested path "absent" means "allow", so a query-backed existence check turns a store fault
into the fail-open direction. The durable read has no such layer between the fault and the answer.

**Why refusing the unknown case costs availability nothing new.** A store that cannot answer this read
cannot answer the existence probe a moment later either, and the create fails on that. So "unknown"
arises only when the create could not have succeeded anyway, and never because of a hub. That is the
property the thread asked for: the refusal is **fail-closed**, and enforcement does **not** vary with
which hubs happen to be warm.

**Why reading the row is not a CQRS violation.** The [CQRS](/Doc/Architecture/CqrsAndContentAccess)
rule forbids reading `Content` off a QUERY row, whose index trails the store; the durable row is what
the index trails. The same page already names `IStorageAdapter.Read(path, options)` as the read that
decides what a node IS for a lifecycle operation, and this create path already probes the same store
(`Exists`, the write guard's durable probes). An absent row answers `null`; it cannot
open the storm-breaker the way a routed point read of an absent node does. The row does trail an
UPDATE of the definition by the owner's save debounce, so a declaration flipped within that window is
judged by its previous value — identically on every process, which the warm-hub alternative cannot
say.

**Cost.** Static types — every platform type, which is nearly every nested create — pay nothing. A
nested create of a type declared in mesh content pays one primary-key read. No hub activation, no
query, no fan-out.

**Proved on a real mesh** by `InMeshPartitionOwnerNestedCreateTest`: a nested create of an in-mesh
owning type is refused with the keyed sentence and the owning type's hub is still not hosted
afterwards (`GetHostedHub(…, HostedHubCreation.Never)`); a top-level create of the same type still
works; a nested create of a non-owning in-mesh type still lands; and with the store made to fault the
one definition read, the nested create is refused as unavailable — while a create of a platform type
under the same parent, which needs no read, is untouched.

### The create FORM, which authored the inconsistency itself

`CreateLayoutArea` forced the namespace to root for a partition-owning type only when `FindStaticNode`
answered, so the form placed an instance of an in-mesh owning type under whatever namespace the field
held. `CreatableTypesProvider` already materialises every offered type's definition to build the
picker, so `CreatableTypeInfo.OwnsPartition` now carries the declaration at no extra read, and the
form forces root for any offered type that owns its partition. That is a convenience that keeps a
person from meeting the refusal — never the boundary. A caller posting `CreateNodeRequest` directly
never sees the form, which is why the validator above is the rule.

### Not changed: the top-level resolver

The four top-level checks still resolve through the listing plus `GetMeshNodeStream`, and so still
carry the cold-activation exposure measured above. The durable row would serve them too. That is a
behaviour change on a separate, security-adjacent path — including the post-creation handler's
independent re-establishment — and is argued on its own rather than folded into this one.

## See also

- [Access Control](/Doc/Architecture/AccessControl) — the partition-owning create rule in full
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the resolution is a
  listing plus a stream read, and never a query for content
- [Creatable Types](/Doc/Architecture/CreatableTypes) — the other declaration a create resolves off
  a NodeType definition
