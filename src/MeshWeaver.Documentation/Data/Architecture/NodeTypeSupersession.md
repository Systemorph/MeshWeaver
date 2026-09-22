---
Name: NodeType Supersession
Category: Architecture
Description: "A retired NodeType names its successor and how its content maps; an instance re-types itself when its hub next activates. Why that removes the migration script, why it converges LAZILY and therefore still needs a sweep, and why the migrating activation must recycle itself."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h11l-2.5-2.5M20 17H9l2.5 2.5"/><circle cx="18" cy="7" r="2"/><circle cx="6" cy="17" r="2"/></svg>
---

# NodeType Supersession

> **The rule in one sentence:** a retired NodeType **declares its successor and how its content
> maps**, and an instance re-types itself the next time its hub activates — so a rename is a
> declaration, not a migration script.

Today a type rename is a hand-run job: find every instance, rewrite `nodeType`, rewrite
`content.$type`, fill the new fields, recycle each address, and hope nobody adds an instance of the
old type while you are doing it. That work is mechanical and the same every time, which is the
signature of something the platform should do.

## The declaration

Supersession lives on the **old** type, because the old type is what a stale instance points at and
therefore the only thing the platform is guaranteed to find:

```jsonc
// Crm/Client.json — the retired type. It STAYS; see "Do not delete the old type".
"content": {
  "$type": "NodeTypeDefinition",
  "supersededBy": "Crm/Counterparty",
  "contentMapping": {
    "$type": "CounterpartyContent",          // the new discriminator, written EXPLICITLY
    "defaults": { "type": "Crm/CounterpartyType/Client" }   // fields the new type adds
  }
}
```

Fields the two content types share carry over by name. Anything the new type adds and the mapping
does not name is left at its own default. Anything richer than that — a field that splits, a value
that must be computed — is a `handler` naming a Code node, resolved exactly as a dispatch rule's
handler is ([Open Vocabularies Are String Constants](/Doc/Architecture/OpenVocabulariesAsStringConstants)).

## When it runs

**On activation, from the hub's own first emission** — the established *wake-up recovery* rule:
drive any non-terminal state to a valid one, exactly once
([Activity Control Plane](/Doc/Architecture/ActivityControlPlane)). "My `nodeType` is superseded" is
exactly such a state.

It is **declarative convergence**, not interruption-sniffing: the hub asks *"is my type retired?"*,
never *"has something been running too long?"*. The owning hub is the single writer for its own
node, so two replicas cannot race, and re-running finds nothing to do.

## 🚨 The migrating activation is still bound to the OLD type

A per-node hub binds its NodeType **once**, while activating, and nothing re-reads it while it lives
([Stale State Until Recycle](/Doc/Architecture/StaleStateUntilRecycle)). So the activation that
performs the migration is *serving the old type the whole time it rewrites the node*: the views, the
layout areas and the content type in force are the retired ones until the address is torn down.

**The migration therefore ends with the node recycling itself**, and the fresh activation binds the
successor. A migration that rewrites the node and stops has produced a node that reads as the new
type and behaves as the old one until something unrelated happens to recycle it — which is worse
than not migrating, because it looks done.

## 🚨 It converges LAZILY — and therefore never finishes on its own

**An instance nobody opens never activates, and therefore never migrates.** A partition that sees no
traffic for a year holds nodes of the retired type for a year. This is the feature's defining limit
and the one thing that must not be forgotten when it is built:

- **You still need the sweep.** "Are we done?" is answered by
  `search 'nodeType:Crm/Client partitions:all limit:200 select:path,name'` and reading its `count`
  against `coverage.partitions` — never by the absence of complaints. The sweep is how the
  denominator is known; lazy migration only shrinks the numerator.
- **A completion report is part of the feature**, not an afterthought: how many instances of the
  retired type remain, and in which partitions. Without it, supersession silently becomes permanent
  dual-typing.
- **An eager pass is a separate, governed operation** — a sweep that activates and migrates the
  stragglers deliberately. Lazy for correctness, eager on demand for completion.

## 🚨 Do not delete the old type

The retired NodeType stays registered until the sweep reads zero. Its `Source/` and `Test/` areas
are **in-mesh C# that no `dotnet build` ever type-checks**
([NodeType Compilation](/Doc/Architecture/NodeTypeCompilation)), so deleting it breaks code the
compiler cannot see, in a way that surfaces only when a portal compiles it at runtime. Retire it as
a declaration; delete it, if ever, as a separate change once the count is zero and stays zero.

## 🚨 A read must not become an unaudited write

Activation is triggered by whoever opens the node, so a migration on activation is **a write caused
by a read**. Three consequences, each of which has a wrong answer that looks right:

- **It runs as SYSTEM, with an audit entry** (`ImpersonateAsSystem`, plus an `_Activity` record
  naming the mapping) — not as the viewer who happened to arrive. Running it as the viewer makes
  convergence depend on who visited and silently skips readers without write permission.
- **A failed migration must not make the node unopenable.** The activation serves the node as-is,
  reports the failure, and leaves the type alone. A migration that can block a read has turned a
  cosmetic rename into an outage.
- **Write the new discriminator EXPLICITLY, and beware defaults that vanish.** A field whose new
  value happens to be its type's default is **omitted by the serializer** — measured 2026-09-21 on a
  live approval, where an explicitly-sent `"status": "Pending"` (the enum's zero member) was not
  stored on a write that otherwise succeeded. For an enum that is survivable, because it
  deserializes back to the same default; for a `$type` discriminator it is not.

## What this does NOT replace

Supersession is for a **mechanical, lossless mapping** — the same record under a new name and shape.
It removes the ceremony, not the governance:

- A change that needs a **human decision** — whether to merge two types, whether a field's meaning
  really is the same, whether a customer-visible record should be re-categorised — still goes
  through an approval. The declaration is only safe because the answer is already known.
- A **lossy** mapping is not supersession. If the new type cannot hold what the old one held, the
  mapping is a decision about what to drop, and that is not a thing to do silently on someone's next
  page load.

## Cross-references

- [Stale State Until Recycle](/Doc/Architecture/StaleStateUntilRecycle) — why the migrating activation must recycle itself.
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — the wake-up recovery rule this follows.
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — why the retired type must stay registered.
- [Open Vocabularies Are String Constants](/Doc/Architecture/OpenVocabulariesAsStringConstants) — how a mapping handler is resolved, and the zero-member hazard.
- [The Communication Hub](/Doc/Architecture/CommunicationHub) — where a governed approval is still the right answer.
