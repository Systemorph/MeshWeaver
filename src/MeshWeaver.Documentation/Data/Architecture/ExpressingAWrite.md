---
Name: Expressing a Write
Category: Architecture
Description: The four ways a mesh-node mutation can be expressed — C# lambda, JSON Patch (plus our text-splice extension), full entity, and the residual "other" — which one each context may use, and the lowering that makes the lambda the authoring surface and the patch the wire format.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h8"/><path d="M4 12h5"/><path d="M4 17h8"/><path d="M15 14l3-3 3 3"/><path d="M18 11v9"/></svg>
---

# Expressing a Write

[Request via Stream Update](../RequestViaStreamUpdate) settles **where** a mutation goes: through
`workspace.GetMeshNodeStream(path).Update(...)`, to the hub that owns the node. This page settles a
different question that sits one level down — **how the change itself is expressed**, and what has to
happen to that expression before the owner can apply it.

There are four shapes. Which one is correct is a property of the **context**, not a matter of taste,
and the whole point of the design is that the first lowers into the second.

## The four patterns

| # | Pattern | Authoring surface | Crosses a process boundary? |
|---|---|---|---|
| **1** | **Lambda expression** — `live => live with { … }` | C#, in a fluent API | ❌ not as a delegate — see below |
| **2** | **JSON Patch** — RFC 6902/7396 plus our text-splice extension | JSON | ✅ this is the wire format |
| **3** | **Full entity** — the complete node, written wholesale | JSON or C# | ✅ |
| **4** | Other — anything a caller cannot say in 1–3 | — | — |

**Prefer 1.** It is the shape that says what the change *means* at the call site, it is type-checked,
and it is the only one that cannot silently write a field the author never mentioned. Pattern 3 is
reserved for the one context where wholesale really is the intent; pattern 4 is a finding, not a
category — if a use case lands there, the gap is in 1 and 2.

## The lowering: 1 → 2 → applied

A delegate cannot be serialized, so a lambda cannot itself be sent to the owning hub. An
**expression tree** can be *analysed*, and what it lowers to is a patch:

```text
  author writes             compiled to              shipped              applied owner-side
  ─────────────             ───────────              ───────              ──────────────────
  Expression<Func<T,T>>  →  JSON Patch ops        →  the wire         →  parsed back, applied
  live => live with {        [ { "op": "add",         (RFC 7396          to the LIVE node inside
    Count = live.Count + 1     "path": "/count",       merge patch)       the owner's serialised
  }                            "value": 1 } ]                             Update lambda
```

Read in reverse, the same relation is what makes pattern 2 a first-class authoring surface in its own
right: a patch that arrives over MCP, the CLI or a webhook is parsed and applied through **exactly the
same owner-side path** an in-process lambda reaches. One execution point, two ways in.

Two properties fall out of this and are the reason for the design:

- **The fold stays owner-side.** The increment is computed against the node *as the owner holds it*,
  inside the serialised `Update`, never against the caller's read. That is what makes concurrent
  writers safe without the caller locking or re-reading.
- **The caller does not have to have read the node.** `count + 1` is expressible without knowing
  `count`. Every shape that requires the caller to know the current value is a stale-read bug waiting
  for a second writer.

### Why the lambda alone is not enough — measured

In-process, pattern 1 already works, and two sites in `src/` do exactly this today:

```csharp
// src/MeshWeaver.PluginCatalog/RegistrationKeyService.cs:131
hub.GetWorkspace().GetMeshNodeStream(keyPath)
    .Update(current => current with
    {
        Content = Content<RegistrationKey>(current) is { } key
            ? key with { UsageCount = key.UsageCount + 1, LastUsedAt = DateTimeOffset.UtcNow }
            : current.Content,
    });
```

```csharp
// src/MeshWeaver.Graph/MeshNodeExtensions.cs:367 — FoldOntoLive
MeshNode FoldOntoLive(MeshNode live) => live with { … Content = record with
    { AccessCount = (live.ContentAs<UserActivityRecord>(o)?.AccessCount ?? 0) + 1 } };
```

Both are correct, and both are confined to the update path. **The shape breaks exactly at the
create-or-update boundary**: `CreateOrUpdateNodeRequest` is an `IRequest<…>` routed to the owning hub,
so a lambda cannot travel with it, and its full-instance mode takes `Content = sourceNode.Content ??
state.Content` — content wholesale, computed by the caller from a read that is stale by construction.
A caller that needs *both* create-if-missing *and* an owner-side fold has no route today, which is the
gap behind the activity-tracking failure (#1174 / #4928). The lowering is what closes it: the caller
writes the lambda, the lambda lowers to patch ops, the ops travel with the request, the owner applies
them inside the `Update` lambda it already runs.

## Pattern 2 in detail — and the one extension we add

Standard JSON Patch covers `add` / `remove` / `replace` / `move` / `copy` / `test`, and RFC 7396 merge
patch covers "these keys change, omitted keys are preserved, a null deletes". Neither can express a
**text splice**: inserting or deleting a run of characters inside a long document without re-emitting
the document. For markdown bodies and code sources that is the dominant edit, and re-emitting is not a
cosmetic cost — it is token spend for an agent and truncation corruption on a long file.

So pattern 2 carries a text-splice extension, and it has **two addressing schemes** which are not
interchangeable:

| Addressing | Shape | Safe when |
|---|---|---|
| **Anchored** | replace exact `oldText` → `newText` | always — the anchor is re-verified against live text |
| **Positional** | insert at (row, col) or string index; delete (position, length) | only when the splice carries a **base fingerprint** the owner checks |

**Anchored is the default, and it is what the agent surface already speaks.** `MeshOperations.EditContent`
(`src/MeshWeaver.Mesh.Operations/MeshOperations.cs:2030`) replaces an exact substring and re-verifies the
anchor *inside* the owner's `Update` against the live node, so a concurrent edit that moved the text
produces an `AnchorNotFoundException` rather than a corrupted splice. A position, by contrast, is
meaningless the moment another writer inserts a character above it — which is why positional splices are
legal **only** as base-fingerprinted operations the owner refuses on mismatch. That machinery already
exists: see the string-splice conflict resolution in [Data Synchronization and CRDT](../DataSyncAndCrdt).

🚨 **Never send a bare position without a base fingerprint.** It is the one shape in this whole design
that fails silently — the write lands, in the wrong place, and nothing errors.

## Choosing, by context

| Context | Pattern | Why |
|---|---|---|
| In-process C#, node exists | **1** | Type-checked, owner-side, no wire hop |
| In-process C#, node may be missing | **1 → 2** | Needs create-if-missing *and* an owner-side fold |
| Counter / tally / "bump this" | **1 → 2** | Must never read-then-write |
| Text edit in a markdown body or code source | **2**, anchored | Splice, not a whole-document rewrite |
| Editor autosave (whole buffer is the truth) | **3** | The buffer *is* the new content |
| **One-way sync source** — GitSync import, plugin install, mirror | **3** | The source is authoritative by definition; there is no local state to fold onto |
| Anything else | **4** → file it | The gap is in 1 or 2 |

Pattern 3's legitimacy is narrow and worth stating plainly: it is correct **when the writer is the sole
authority for that content**. A one-way sync source is exactly that — the repo, the package or the
upstream portal defines the node, and a local edit is not something to preserve. Outside that, a
full-entity write is how one writer silently reverts another's field, because every field it did not
mention is still *written* (see [Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs)).

## What each surface speaks

| Surface | Verb | Pattern today |
|---|---|---|
| In-process C# | `GetMeshNodeStream(path).Update(lambda)` | **1** |
| In-process C# | `CreateOrUpdateNodeRequest` (full instance) | **3** |
| Agent tool / MCP | `update` | **3** — full replace |
| Agent tool / MCP | `patch` | **2** — RFC 7396 merge, top-level fields + deep-merged content |
| Agent tool / MCP | `edit_content` | **2** — anchored text splice |
| CLI (`memex`) | `update` / `patch` | **3** / **2** |

Two gaps are visible in that table and both are real:

- **No surface outside C# can express a fold.** `patch` needs the caller to compute the new value,
  which means reading first. An agent bumping a counter over MCP has the stale-read problem the
  in-process lambda does not.
- **The CLI has no anchored-edit verb.** `memex patch` can replace a whole content field; there is no
  `memex edit-content`, so a scripted text edit re-emits the document.

## Status

**This page describes the design. The lowering (1 → 2) is not implemented.** What exists today is the
right-hand column of the surface table above, plus the two in-process folds quoted earlier.
`CreateOrUpdateNodeRequest.Patch` exists as a property and is **refused by the handler**
(`MeshExtensions.cs:5836`, *"Patch-mode upserts are not yet supported"*) with zero callers in either
repo — so patch-mode upserts are not a limited feature, they are an unbuilt one.

Tracked on **#4928** (the verb gap) and **#1174** (the production fault it blocks).

## Related

- [Request via Stream Update](../RequestViaStreamUpdate) — where a write goes
- [Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs) — what an absent field means in a patch
- [Data Synchronization and CRDT](../DataSyncAndCrdt) — version + string-splice conflict resolution
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why a query must never decide a write
- [Data Access Patterns](../DataAccessPatterns) — the verb table
