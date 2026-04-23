---
NodeType: "Doc/Article"
Title: "CQRS — Queries vs. Content Access"
Abstract: "Why you must never use a query to fetch a specific node's content. Queries return sets of matches and can lag behind writes; reading content uses GetRemoteStream with a MeshNodeReference — direct, reactive, and always in sync."
Icon: "Split"
Published: "2026-04-23"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "CQRS"
  - "Queries"
  - "Streams"
  - "Consistency"
---

## The rule

> **Queries bring sets of elements. Nothing more.**
> If you want the *content* of a specific node, **never use a query** — use
> `workspace.GetRemoteStream<MeshNode>(address, new MeshNodeReference())`.

This is not a stylistic choice. It is a correctness requirement.

## Why queries can't be used for content

MeshWeaver separates **read-side discovery** (queries) from **authoritative content
reads** (workspace streams). They go through different code paths with different
guarantees:

| | Query (`QueryAsync`, `ObserveQuery`) | Content access (`GetRemoteStream` + `MeshNodeReference`) |
|---|---|---|
| Purpose | Find WHICH nodes match a predicate | Get THE current state of a known node |
| Returns | A set (0..N items) | A single `MeshNode` |
| Source | Read-side index / cached projection | Owning hub's workspace (source of truth) |
| Consistency | Eventually consistent — **has a delay** | Strong — emits the hub's current committed state |
| After a write | May still return the old row for milliseconds | Next emission reflects the write |
| Cost | Scans / indexed lookup across partitions | One subscribe on the owning hub |
| Scales | Thousands of rows per second | One stream per caller per node |

A query that "happens to match one row" is still a query. It ran through the indexed
read path. It can lie about the current state. It can be out of sync.

The indexed/cached read path exists for good reasons — it makes `Search("nodeType:Agent")`
fast across millions of nodes. But that same indirection is exactly why you must not
use it to read a node you already have the path for.

## The two patterns

### Query — only when you're looking for a set

```csharp
// "Give me every Agent node under this namespace"
mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Agent namespace:OrgA"))
    .Take(1)
    .Subscribe(change =>
    {
        foreach (var node in change.Items) { /* paths only — existence + metadata */ }
    });
```

Valid use cases:
- Listing children of a namespace (`path/*`)
- Searching by predicate (`nodeType:X`, `name:*sales*`)
- Checking whether any match exists
- Browsing / autocomplete

What you get back is enough to *decide what to read next*. It is **not** what you
render to the user or base a business decision on.

### Content — always through `GetRemoteStream` + `MeshNodeReference`

```csharp
// "Give me the live state of THIS node"
var workspace = hub.GetWorkspace();
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(path), new MeshNodeReference())
    .Take(1)
    .Subscribe(change =>
    {
        var node = change.Value;  // current committed MeshNode, not a lagged index hit
        if (node is null) { /* truly not found */ return; }
        // work with node.Content, node.Version, etc.
    });
```

Valid use cases:
- Getting a node for rendering
- Reading content before a Patch / Update (merge semantics)
- Anything where staleness would be a bug

`GetRemoteStream` is cached per `(address, reference)` pair, so repeated calls reuse
one subscription to the owning hub. Drop the subscription when you're done —
the stream stays live until all subscribers unsubscribe.

## The composite pattern: find-then-read

When you only know the node by name/predicate, do both:

```csharp
// 1) Query to discover existence + path
mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"name:\"{displayName}\" nodeType:Report"))
    .Take(1)
    .SelectMany(change =>
    {
        var hit = change.Items.FirstOrDefault();
        if (hit is null) return Observable.Return<MeshNode?>(null);

        // 2) Content access for the node's actual current state
        return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(hit.Path), new MeshNodeReference())
            .Take(1)
            .Select(change => change.Value);
    })
    .Subscribe(node => { /* authoritative node — or null if not found */ });
```

This shape costs two round trips but is always correct. Shortcutting by using
`change.Items.FirstOrDefault()` directly from the query and treating it as the
content is the bug this article exists to prevent.

## Why the delay matters — a concrete example

```text
t=0    ms   agent writes:  UpdateNode @OrgA/Report, Content = { Title: "Q2" }
t=5    ms   agent reads:   mesh.QueryAsync("path:OrgA/Report").First()
             → returns Content = { Title: "Q1" }  ← STALE
             → the read-side index hasn't been updated yet
t=40   ms   read-side index catches up
t=41   ms   same query now returns { Title: "Q2" }
```

Meanwhile, in the same scenario, `GetRemoteStream` subscribes directly to the
owning hub's workspace, which applied the write synchronously — the next emission
has `{ Title: "Q2" }` with no staleness window.

In an AI agent, the staleness window is where the agent re-reads its own Patch
and thinks it didn't take — then patches again, double-writes, and the user
sees a mess. Using `GetRemoteStream` eliminates the class of bug.

## Watching for updates (wait-for-completion)

`GetRemoteStream` is not a one-shot fetch — it's a **live subscription** to the
node's workspace. Every write on the owning hub pushes a new emission. This is
what makes it the right tool for *waiting until something happens*:

```csharp
// Run a script and wait until it reports completion in its progress state.
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(jobPath), new MeshNodeReference())
    .Where(change =>
        change.Value?.Content is JobStatus { State: "Done" or "Failed" })
    .Take(1)
    .Timeout(TimeSpan.FromMinutes(5))
    .Subscribe(
        final => logger.LogInformation("Job finished: {State}",
                    ((JobStatus)final.Value!.Content!).State),
        err   => logger.LogError(err, "Job did not finish in time"));
```

Key point: the stream stays live for the subscription's lifetime. The first
emission is the current state; subsequent emissions arrive as the hub applies
writes. `Where(...).Take(1)` waits until the condition is true, at which point
the stream completes naturally.

This is the correct primitive for any "kick off work and notify me when done"
pattern — no polling, no `await` on a long-running `Task`, no hub blocking.
Compare to a query-poll loop, which would also re-hit the lagged read path on
every tick.

## Scopes in queries

Some people try to dodge staleness with `scope:exact` on a query targeting one
node. **This does not change the source** — it still flows through the query
read path. The index is still the index. The delay is still there.

`scope:*` is a filter on which matches to return, not a switch between two read
paths. There is no query scope that upgrades to strong consistency.

## The `MeshNodeReference` family

`MeshNodeReference` is a `WorkspaceReference<MeshNode>` that represents
"the hub's own MeshNode." You pass it to `GetRemoteStream` to read content:

```csharp
// Remote read (cross-hub — owner ≠ current hub)
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(otherHubPath), new MeshNodeReference());

// Local read (same hub — from inside a layout area / handler on the owning hub)
hub.GetWorkspace().GetStream(new MeshNodeReference());
```

Other references exist for different shapes of content
(`LayoutAreaReference`, `CollectionReference<T>`, `PartialWorkspaceReference<T>`);
see *Workspace references* for the catalogue. `MeshNodeReference` is the one you
want for "read the MeshNode itself."

## Quick decision matrix

| Your intent | Use |
|---|---|
| "List all nodes under X" | `ObserveQuery` |
| "Does a node called X exist?" | `ObserveQuery` + `Take(1)` + check `Items.Count` |
| "Give me the Report node at `@OrgA/Q2Report`" | `GetRemoteStream<MeshNode, MeshNodeReference>` |
| "What's the current value of this field on this specific node?" | `GetRemoteStream` |
| "Patch this node — I need to merge with current content first" | `GetRemoteStream` to read, then `mesh.UpdateNode` to write |
| "Autocomplete: what nodes start with `Sal`?" | `ObserveQuery` |
| "Render the page for this node" | `GetRemoteStream` |

## Anti-patterns

```csharp
// ❌ Query to get content — stale read, guaranteed bug surface.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
return JsonSerializer.Serialize(node);

// ❌ Same in reactive clothing — still a query, still stale.
return mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
    .Take(1).Select(c => c.Items.FirstOrDefault());

// ❌ Wrapping QueryAsync in Observable.FromAsync does not make it strongly consistent.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ✅ Content read — goes to the owning hub's workspace, no staleness.
return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(path), new MeshNodeReference())
    .Take(1)
    .Select(change => change.Value);
```

## Summary

- **Queries return sets.** Use them to discover existence and to enumerate.
- **Content comes from workspace streams.** Use
  `GetRemoteStream(address, new MeshNodeReference())` for a single node.
- A query that "only returns one" is still a query. It still lags.
- If your code is about to call `.FirstOrDefault()` on a query result and use
  the node's `.Content`, you are writing a bug. Switch to `GetRemoteStream`.

Related reading:
- [Asynchronous Calls](AsynchronousCalls) — the hub's single-threaded scheduler and
  why `await` deadlocks it.
- [Workspace references](WorkspaceReferences) — catalogue of `WorkspaceReference<T>`
  shapes and what each one emits.
- [Data access patterns](DataAccessPatterns) — which DI service to use for what.
