---
Name: Expressing a Write
Category: Architecture
Description: The four ways a mesh-node mutation can be expressed — C# lambda, a patch document (plus our fold and text-splice extensions), full entity, and the residual "other" — which one each context may use, and the lowering that makes the lambda the authoring surface and the patch the wire format.
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
| **2** | **A patch document** — RFC 7396 merge *or* RFC 6902 ops, plus our fold and text-splice extensions | JSON | ✅ this is the wire format |
| **3** | **Full entity** — the complete node, written wholesale | JSON or C# | ✅ |
| **4** | Other — anything a caller cannot say in 1–3 | — | — |

🚨 **"Patch" names two different envelopes here, and sending the wrong one is a refusal, not a
merge.** They are not interchangeable:

| | RFC 7396 — *merge patch* | RFC 6902 — *JSON Patch* |
|---|---|---|
| shape | a partial **document**: `{"content":{"status":"done"}}` | an **array of ops**: `[{"op":"replace",…}]` |
| deletes by | a `null` member | an explicit `remove` op |
| used by | `stream.Update`'s cross-hub transport; the `patch` verb on MCP/CLI | `CreateOrUpdateNodeRequest.Patch` (typed for `Json.Patch`) |

So the verb called `patch` and the property called `Patch` do **not** take the same payload. Where
this page says "patch ops" it means RFC 6902; where it says "merge patch" it means RFC 7396.

**Prefer 1.** It is the shape that says what the change *means* at the call site, it is type-checked,
and it is the only one that cannot silently write a field the author never mentioned. Pattern 3 is
reserved for the one context where wholesale really is the intent; pattern 4 is a finding, not a
category — if a use case lands there, the gap is in 1 and 2.

## The lowering: 1 → 2 → applied

A delegate cannot be serialized, so a lambda cannot itself be sent to the owning hub. An
**expression tree** can be *analysed*, and what it lowers to is a patch:

```text
  author writes                  lowered to                shipped         applied owner-side
  ─────────────                  ──────────                ───────         ──────────────────
  Expression<Func<T,T>>       →  RFC 6902-shaped ops    →  the wire     →  parsed back, applied
  live => live with {            using the FOLD                            to the LIVE node inside
    Count = live.Count + 1         extension:                              the owner's serialised
  }                              [ { "op": "fold",                         Update lambda
                                     "rule": "sum",
                                     "path": "/count",
                                     "value": 1 } ]
```

🚨 **The op must be `fold`, not `add`.** RFC 6902's `add` with `"value": 1` *sets* `/count` to 1 — it
does not evaluate `live.Count + 1`. Standard JSON Patch has no increment, which is precisely why
pattern 2 needs an extension rather than just a mapping; a lowering onto stock ops would document a
write that **loses the count**. Same reason the text splice is an extension. Neither op exists yet —
see *Status*.

Read in reverse, the same relation is what makes pattern 2 a first-class authoring surface in its own
right: a patch that arrives over MCP, the CLI or a webhook is parsed and applied through **exactly the
same owner-side path** an in-process lambda reaches. One execution point, two ways in.

Two properties fall out of this and are the reason for the design:

- **The fold is evaluated where the node lives.** A `fold` op names the *rule* and the *operand*, not
  a computed result, so the owner performs the arithmetic against its own serialised state. Two
  concurrent `sum 1` ops compose; two concurrent `set 6` do not.
- **The caller does not have to have read the node.** `count + 1` is expressible without knowing
  `count`. Every shape that requires the caller to know the current value is a stale-read bug waiting
  for a second writer.

### 🚨 Why this matters more than the upsert gap: a cross-hub lambda already loses counts

It is tempting to read pattern 1 as "in-process, therefore owner-side, therefore safe". It is not —
and [Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs) carries the mechanic:

| the node | when the lambda runs | what travels |
|---|---|---|
| **owned** by this hub | on the owner's serialised write path | the node |
| **not owned** by this hub | on **this hub's mirror** | an RFC 7396 **merge patch** |

For a node this hub does not own, `live.Count + 1` is computed against the *mirror's* snapshot and
what ships is the **resulting value**. Two mirrors folding from the same base both send `{"count": 6}`,
the owner merges both, and one increment is gone. That disjoint-patches argument — the reason
concurrent writers are normally safe — holds for writers touching *different* members, and a counter
is the case where they touch the same one.

So the fold gap is not only at the create-or-update boundary that blocks #1174; it is at **every
cross-hub write of a value derived from the node's own state**. A `fold` op closes both, because the
rule and the operand survive the trip where a computed value does not.

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

Both are confined to the update path, and — per the section above — both are safe only to the extent
that the node is **owned by the hub running the lambda**; where it is not, each is a candidate for the
cross-hub lost update. **The shape then breaks completely at the
create-or-update boundary**: `CreateOrUpdateNodeRequest` is an `IRequest<…>` routed to the owning hub,
so a lambda cannot travel with it, and its full-instance mode takes `Content = sourceNode.Content ??
state.Content` — content wholesale, computed by the caller from a read that is stale by construction.
A caller that needs *both* create-if-missing *and* an owner-side fold has no route today, which is the
gap behind the activity-tracking failure (#1174 / #4928). The lowering is what closes it: the caller
writes the lambda, the lambda lowers to patch ops, the ops travel with the request, the owner applies
them inside the `Update` lambda it already runs.

## Pattern 2 in detail — the text-splice extension

Standard JSON Patch covers `add` / `remove` / `replace` / `move` / `copy` / `test`, and RFC 7396 merge
patch covers "these keys change, omitted keys are preserved, a null deletes". Neither can express a
**text splice**: inserting or deleting a run of characters inside a long document without re-emitting
the document. For markdown bodies and code sources that is the dominant edit, and re-emitting is not a
cosmetic cost — it is token spend for an agent and truncation corruption on a long file.

So pattern 2 carries a text-splice extension — the second of the two it needs, alongside `fold` — and
it has **two addressing schemes** which are not interchangeable:

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

🚨 **Never send a bare position without a base fingerprint.** It is the only shape that silently
targets the **wrong location** — the write lands, somewhere else, and nothing errors. It is not the
only silent failure on the page: a full-entity write silently **reverts** a field (below), and a
cross-hub fold silently **loses a count** (above). Three different silences, three different causes.

## Choosing, by context

| Context | Pattern | Why |
|---|---|---|
| In-process C#, node exists, **owned by this hub** | **1** | Type-checked, runs on the owner's write path |
| In-process C#, node exists, **owned elsewhere** | **1**, but see the fold caveat above | The lambda runs on the mirror; a derived value can be lost |
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
| In-process C# | `CreateOrUpdateNodeRequest` (full instance) | **3**, but not a pure one — see note |
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

🚨 **`CreateOrUpdateNodeRequest` is not the same "full entity" the `update` verb is**, and the
difference decides whether an omitted field survives. Its update leg merges through
`UpdateAccordingToSourceNode`, which **null-coalesces** most top-level fields against the live node
(`Name = sourceNode.Name ?? state.Name`, and the same for `NodeType`, `Icon`, `Category`,
`Description`, `Order`, `ExcludeFromContext`, `PreRenderedHtml`), gives `State` and `MainNode` their
own rules, and takes only **`Content`** wholesale. So an omitted top-level field is *preserved* here
and *overwritten* by `update` on MCP/CLI. `Content` is the field that behaves like pattern 3 — which
is exactly the field a fold needs.

## Status

**The fold half of the lowering is BUILT; the text splice and the general expression lowering are
not.**

| piece | state |
|---|---|
| `fold` ops on the upsert (`CreateOrUpdateNodeRequest.Folds`) | ✅ built — `Sum` / `Max` / `Min` / `KeepExisting` |
| the fluent authoring surface (`WithFolds<T>(… f => f.Sum(r => r.Count, 1))`) | ✅ built — member-access expressions lower to ops |
| a general `Expression<Func<T,T>>` → ops compiler | ❌ not built; the fluent builder covers the cases that exist |
| the text-splice extension as a patch op | ❌ not built — `edit_content` remains the anchored surface |
| patch-mode upserts (`CreateOrUpdateNodeRequest.Patch`) | ❌ still refused by the handler |

🚨 **What the fold does and does not buy, stated because the stronger claim is the tempting one.**
It makes a counter **caller-read-free**: `Sum 1` is expressible without knowing the count, so one
upsert both creates the node when absent and folds onto it when present, and no stale caller
snapshot decides the value. It does **not** make the counter cluster-atomic — the upsert's own write
still leaves its hub as an RFC 7396 merge patch carrying the folded *result*, so the cross-hub
lost-update above is untouched. Closing that means making a fold survive the cross-hub hop, which is
a change to the sync protocol, and it is deliberately not in this change.

Tracked on **#4928** (the verb gap, now closed for folds) and **#1174** (the production fault, which
additionally needs its caller moved off the query index).

## Related

- [Request via Stream Update](../RequestViaStreamUpdate) — where a write goes
- [Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs) — what an absent field means in a patch
- [Data Synchronization and CRDT](../DataSyncAndCrdt) — version + string-splice conflict resolution
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why a query must never decide a write
- [Data Access Patterns](../DataAccessPatterns) — the verb table
