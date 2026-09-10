---
NodeType: Markdown
Name: "Node Identity — (namespace, id) is the key, path is derived"
Abstract: "A node's identity is the (namespace, id) pair; path is a GENERATED column derived from it. Writes upsert on the key, reads and deletes address the path — so moving a slash between namespace and id leaves the path identical while changing the key, which inserts a duplicate row instead of updating. An id may contain '/', and re-keying one is data loss."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#5e35b1'/><path d='M6 8h12M6 12h12M6 16h7' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='17.5' cy='16' r='2.5' fill='none' stroke='white' stroke-width='1.6'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Data"
  - "Identity"
  - "Postgres"
---

A mesh node's identity is the pair **`(namespace, id)`**. Its **`path` is derived**, not stored
independently — in Postgres it is literally a generated column:

```sql
CREATE TABLE IF NOT EXISTS mesh_nodes (
    namespace       TEXT        NOT NULL DEFAULT '',
    id              TEXT        NOT NULL,
    path            TEXT        GENERATED ALWAYS AS (
                        CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                    ) STORED,
    ...
    PRIMARY KEY (namespace, id)
);
```

Two consequences follow, and together they are the whole of this page.

## An id MAY contain a slash

There is **no constraint anywhere forbidding it**. Every `mesh_nodes` DDL — the three Postgres
variants, the satellite-table script, `mesh_node_history`, and the SQLite adapter — declares plain
`id TEXT NOT NULL`. A repo-wide sweep for a SQL `CHECK` constraint across both `src/` trees finds
none at all.

Slash-bearing ids are not an accident to be tolerated; several node families depend on them. **Every
`LanguageModel` node's id is the provider's wire id** — `z-ai/glm-5.3`, `anthropic/claude-opus-5`,
`openai/gpt-5.2` — because that string is what the provider's API expects and what the model is
known by. The Postgres adapter states the rule in its own words (issue #2212):

> 🚨 THERE IS NO POSITIONAL (namespace, id) SPLIT OF A PATH — an id may contain '/'.

## Splitting a path positionally is path-invariant and key-destroying

Because `path` is `namespace || '/' || id`, **moving a slash from the id into the namespace leaves
the path byte-identical while changing the primary key.** These two rows have the same path and
different identities:

| namespace | id | generated path |
|---|---|---|
| `Provider/OpenRouter` | `z-ai/glm-5.3` | `Provider/OpenRouter/z-ai/glm-5.3` |
| `Provider/OpenRouter/z-ai` | `glm-5.3` | `Provider/OpenRouter/z-ai/glm-5.3` |

That invariance is what makes the corruption invisible: every path-addressed read, every log line,
every URL keeps reading the same. Nothing looks wrong.

## The read/write asymmetry that turns it into data loss

The two sides of the adapter address rows differently, and both are correct in isolation:

- **Writes upsert on the KEY** — `INSERT … ON CONFLICT (namespace, id) DO UPDATE SET …`
- **Reads and deletes address the PATH** — `SELECT … WHERE path = $1`, `DELETE … WHERE path = $1`

So a write carrying a re-keyed `(namespace, id)` finds **no conflict** and **INSERTs a second row**
whose generated path collides with the first. From that moment:

1. A read `WHERE path = $1` matches **two** rows and resolves an arbitrary one — not reliably the
   same one twice.
2. The versions table (PK `(namespace, id, version)`) holds **two independent chains**, so a node
   with a long history can read back as having none.
3. A delete `WHERE path = $1` removes **BOTH** rows. One delete, and the node is gone.

## What went wrong (#3894)

`MeshOperations.SanitizeNodeId` split a slash-bearing id at its LAST slash and moved the prefix into
the namespace, on the stated premise that *"the DB has a CHECK constraint blocking slashes in id"*.
That premise was false, and had presumably always been false. `Create` and `Update` both ran through
it, so **no MCP write could address a flat-keyed slash-id node** — every write minted a duplicate.

On the production portal this split `Provider/OpenRouter` + `z-ai/glm-5.3` across two rows on
2026-09-09; the following morning the node was gone from the model list entirely. The MCP `create`
tool's own documentation stated the same false rule (*"id — the node's own slug, NO slashes"*), so
an agent following its instructions produced the duplicate by hand.

Both writes reported *"did not land within the confirmation window"* — a **true** negative: the
confirmation reads the flat-keyed path while the write had gone to a different primary key.

`Patch` was never affected. It reads the existing node and writes `existing with { … }`, so it
inherits that node's keying by construction — which is why the split could not be reproduced through
`patch` alone.

## The rules

- **Never split a path positionally into `(namespace, id)`.** Not in an adapter, not in a tool, not
  in a "normalisation" step. The decomposition the caller holds is the correct one.
- **Address a row by `path`.** It is the only decomposition-free way to name a row, it is what the
  caller actually has, and it is indexed.
- **A write to an existing node carries back the SAME `(namespace, id)` it was read with.** Re-deriving
  identity from the path on the way in is how a duplicate gets created.
- **Never "tidy" an id.** Store it verbatim, slashes included.

## The adjacent failure: a cached miss that outlives its invalidation

Worth knowing when a node "does not exist" right after you created it. A negative read is cached by
the **storm breaker inside `MeshNodeStreamCache`** (`_negative`, keyed by path) — not by the per-node
hub, and not by the unrelated `MessageStormBreaker`, which is a per-hub *rate* breaker with no path
negative. An open window fast-fails **reads and writes alike**, and `MeshOperations.Get` reports it
as `Not found`, indistinguishable from genuine absence and produced without ever reaching the owner.

A create at that path **does** clear it: the post-commit `MeshChangeEvent.Created` publish reaches
`MeshNodeStreamCache.OnMeshChange` → `ResetFailureState(path)`, which drops the negative entry and
evicts a faulted read entry. That behaviour is pinned by a test.

The gap is a race, not a missing mechanism (tracked as **#3954**): `RecordNegative` writes `_negative[path]` unconditionally,
with no epoch or claim guard. A `NotFound` minted by a read that subscribed *before* the create can
therefore land *after* the `Created` broadcast has already reset the state, re-arming a window of up
to five minutes that nothing then evicts until it expires — or until another change event for that
path arrives. A `recycle` publishes exactly such an event, which is why recycling clears the symptom.
`PathResolutionService` guards the identical race with its `_pendingFills` claims; the stream cache
has no counterpart.

## See also

- [CQRS — Queries, Reads, Writes, Operations](/Doc/Architecture/CqrsAndContentAccess) — why a point
  read of a maybe-absent path is a defect, and what to use instead.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per
  partition, and why `namespace` keeps its partition prefix.
- [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) — the read/write API table.
