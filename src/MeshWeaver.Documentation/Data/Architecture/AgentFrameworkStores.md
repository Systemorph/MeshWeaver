---
NodeType: Markdown
Name: "Agent Framework Stores — mesh nodes behind Microsoft's abstractions"
Abstract: "MeshWeaver already builds on Microsoft Agent Framework's ChatClientAgent. Its 1.0 release turned every stateful part of an agent loop into a pluggable store, which is exactly the seam we need: we implement those abstractions over mesh nodes instead of the file system, so an agent's working files and skills are versioned, permissioned, first-class content. This page is the contract those implementations follow — reactive end to end, pooled, and identity-carrying."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4527a0'/><path d='M6 7h12v3H6zM6 12h8v3H6zM6 17h5v2H6z' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "AI"
  - "Agents"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls), [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) and [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess). This page assumes all three.

## Where we already were

MeshWeaver has built on **Microsoft Agent Framework** (`Microsoft.Agents.AI`) for a while: `ChatClientAgentFactory` produces a `ChatClientAgent`, and `AgentSession` / `AgentResponse` carry a round. What we had *not* taken is the layer added at 1.0 — the point at which the framework stopped baking its stateful pieces in and turned each one into an abstract, pluggable store.

That change is what makes reuse possible. Each abstraction is small, and each one is a place where "where does this live?" becomes our decision instead of theirs.

| Abstraction | Shape | Our implementation |
|---|---|---|
| `AgentFileStore` | 7 abstract methods (read / write / delete / list / exists / search / mkdir) | `MeshNodeAgentFileStore` |
| `AgentSkillsSource` | 1 abstract method returning `AgentSkill`s | `MeshAgentSkillsSource` |
| `ChatHistoryProvider` | supplies + stores a round's messages | *not yet — see below* |
| `AIContextProvider` | injects instructions / messages / tools per round | *not yet — see below* |

## Why implement theirs instead of inventing ours

The framework's own stores are a directory on disk (`FileSystemAgentFileStore`) or a dictionary (`InMemoryAgentFileStore`). Both are invisible to everything else: files an agent writes are not versioned, not permissioned, not searchable, and gone when the pod is. Implementing the same abstraction over mesh nodes means an agent's working files *are* ordinary content — and code holding only an `AgentFileStore` reference keeps working, unaware it is talking to a mesh.

## The contract every store follows

Three things must be true of every store method, and getting any of them wrong fails **silently** — a denied write, a leaked pool slot, a stale read. They are therefore not left to each implementation: they live in `MeshStoreAccess`, which each store **composes** (each MAF store type is an abstract *class*, so it already occupies the base slot).

### 1. Reactive end to end

Our API is `IObservable<T>` throughout. `Task` appears only in the `*Async` members MAF declares abstract, and each of those is a **one-line adapter** over the reactive method — no `async`, no `await`, one `ToTask` per call. The only `await` in the whole path is the one sealed inside `IIoPool`.

Where MAF's signature can carry a single value and our shape is live, the `.Take(1)` sits **at that boundary**, never in the reactive method — so the live shape stays live for every other consumer.

### 2. All I/O through the pool — and not the agent's pool

Every mesh call runs on `IoPoolNames.AgentStore`, deliberately **separate from `IoPoolNames.Ai`**. A store call happens inside a tool call, which happens inside an agent round that is *already holding an `Ai` slot*. Re-entering the same bounded pool from within a slot it already holds is the classic nested-gate deadlock: at the cap, every holder waits for a slot only a holder can release. A separate pool makes the nesting acyclic.

The two pooling primitives are not interchangeable:

- `Once(...)` → `IIoPool.InvokeObservable` — one-shot work (a single-node read, a write). Holds a slot until the source **completes**.
- `Stream(...)` → `IIoPool.SubscribeThroughPool` — a live query or node stream, which re-emits and **never completes**. Pointing `Once` at one of these leaks a slot per call *and* collapses a live query into a snapshot.

### 3. Identity is captured at construction, not per call

MAF invokes a store from wherever its own loop happens to be — a continuation thread with no `AsyncLocal` flow. By the time the operation runs there is no ambient identity left to read. A store is constructed **per agent round, on the hub, while the round's identity is current**; that is the only moment the real principal is observable. Every operation then re-stamps it around the mesh call.

This ordering is load-bearing: `MeshNodeStreamHandle.Update` captures `AccessService.Context` when the observable is **built**, so the identity scope has to be in force while the chain is composed — which is why the pooled helpers take a *factory*, not an observable. Without it every store write runs with a null context and is denied by owner-side RLS. See [Access Context Propagation](/Doc/Architecture/AccessContextPropagation).

## The file store

`MeshNodeAgentFileStore` is rooted at a mesh path. A store-relative `notes/x.md` becomes the node at `{root}/notes/x.md`. Files are `Markdown` nodes carrying `MarkdownContent`; directories are `Group` nodes. Nesting is implicit in the path, so a file written into a never-created directory still lands correctly.

**Writes split by lifecycle, and this is not a detail.** `stream.Update` is *the* mutation API, and overwrite uses it — the owning hub serialises every writer through its single-threaded action block, and a cross-hub write sends only the RFC 7396 merge patch. But `stream.Update` targets a node's per-node hub, and for a path that does not exist yet **that hub never activates**: the write comes back `DeliveryFailure: No node found`. Bringing a node into being is node *lifecycle*, which is what `CreateNode` is for. So: probe → existing means mutate, absent means create.

**Search is a query, so it is live — and the snapshot is taken at the boundary.** `Search(...)` returns `IObservable<IReadOnlyCollection<FileSearchResult>>` and keeps re-emitting as matching content changes; that is the shape MeshWeaver code binds to. `SearchAsync` is the same one-expression adapter as every other override — `.Take(1)` then `ToTask` — because MAF's signature carries a single value. The rule is uniform: the `.Take(1)` lives at the MAF boundary, never in the reactive method.

## The skills source

`MeshAgentSkillsSource` serves our existing `nodeType:Skill` nodes. Nothing about how skills are authored or stored changes.

Discovery goes through `AiSettingsNodeType.ObserveSkillQueries` — **the same call the chat's slash autocomplete and slash execution make**. That sharing is the point, not an implementation detail: the skills a user sees listed are exactly the skills an agent round resolves, *including* sources the user configured or a skill package installed. Nothing reconstructs these query strings.

Two definitions have to agree for that to hold, and both are pinned by tests: `AiSettingsNodeType.DefaultSkillQueryTemplates` (what the settings path resolves when a user has configured nothing) and `AgentPickerProjection.BuildSkillQueries` (the canonical builder). If they drift, a user sees skills an agent does not have — silently.

The layers, one query row each:

- **the platform defaults** — `Skill`, always **first** (see below);
- **the user's own** — `{user}/Skill`, a flat namespace;
- **the context node's partition** — the whole subtree;
- **the node type's partition** — the whole subtree.

The two partition layers are wider than the user's on purpose. A user's skills sit where convention puts them. A space or a plugin ships skills wherever its content is organised — beside the types they describe, under feature folders, several levels deep. Requiring a flat `{partition}/Skill` namespace means a skill authored next to the thing it explains is simply never found. Scoping those layers to the partition subtree makes placement a content decision again, at the cost of one extra query per layer.

**Row order is not the precedence signal.** When two layers define the same skill name the more specific one wins — that is what lets a user override a platform skill — and it is resolved from each result's *own partition*, never from the order of the query rows. Order matters for one unrelated reason: the platform row is the only one guaranteed to resolve (every other targets a partition that may not exist), so it stays first. Demoting it makes slash autocomplete surface nothing, which `SkillAutocompleteTest` catches.

**One live query, no per-skill reads.** The synced `GetQuery` collection carries whole nodes, content included, so listing N skills costs one shared subscription and `GetContentAsync` performs no I/O at all.

## How agents get these capabilities

Through the **existing** plugin path, unchanged. `AgentFilesPlugin` is an ordinary `IAgentPlugin` resolved by name in `ChatClientAgentFactory.ResolvePluginTools`, exactly like `Version`, `Collaboration` or `Lsp`. An agent opts in with `plugins: [AgentFiles]` in its frontmatter.

We do **not** route through MAF's harness loop or its tool injection. The tool-call architecture is ours and stays ours; what we adopted is the *storage* abstraction underneath it.

## What is deliberately not adopted yet

`ChatHistoryProvider` and `AIContextProvider` are the natural next seams — `ThreadExecution.LoadFullConversationHistoryFromMesh` is precisely the thing `ChatHistoryProvider` abstracts, and our context projections are `AIContextProvider`s in all but name. Both mean refactoring the round pipeline rather than adding to it, so they are separate work. Compaction (`CompactionStrategy`, `MaxContextWindowTokens`) is the other outstanding win: `AgentChatClient` currently truncates by character budget and says so.
