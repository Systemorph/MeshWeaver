---
NodeType: Markdown
Name: "Designing agents"
Abstract: "Practical, transferable patterns for building cost-effective, correct agents on MeshWeaver — the turns × context cost model, what survives a round boundary, the two-phase extract-then-write protocol, hard vs. soft enforcement, and write-phase discipline for fact/dimension models."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><path d='M6 7h12M6 12h12M6 17h8' stroke='white' stroke-width='1.8' stroke-linecap='round'/><circle cx='18.5' cy='17' r='2.2' fill='#80cbc4'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Agentic AI"
  - "Cost"
  - "Best Practices"
---

> **Scope:** this page is the *practices* layer — how to design an agent that is both cheap and correct. For the philosophy see [Agentic AI](/Doc/AI/AgenticAI); for the technical wiring (agent definitions, orchestration, MCP) see [Agentic AI Architecture](/Doc/Architecture/AgenticAI); for the tool call shapes see [MeshPlugin Tools](/Doc/AI/Tools/MeshPlugin).

The patterns here were distilled from tuning a real document-extraction agent — one that reads a 60–80-page financial report from a content collection and writes ~60 typed fact nodes into a dimensional model. Iterating the same task from **~$14 down to under $2 per run**, with correctness *improving* at the same time, came down to a handful of framework behaviours that are easy to get wrong. They generalise well beyond extraction.

## 1. The cost model: turns × context

Every tool call is a model **turn**, and every turn re-sends the **entire conversation so far** — the instructions, all prior tool results, and all intermediate text. Cost is therefore multiplicative:

> **cost ≈ (number of turns) × (context carried per turn)**

The two levers fall straight out of that product:

- **Reduce turns.** Batch tool calls; avoid per-item loops. Sixty creates issued one-per-turn is sixty turns; the same sixty creates emitted as a few parallel batches is a handful of turns.
- **Keep heavy artifacts out of the context during high-turn phases.** A full document or a large reference table that sits in context while the agent issues sixty create calls is re-billed on *every one* of those turns.

Worked example: ~60 `Create` calls issued one per turn, on top of a context that still holds a full document, re-bills that document ~60 times. The identical work with the document **absent** and the creates **batched** costs roughly a tenth as much. The next two sections are how you make the document "absent" without losing the information in it.

## 2. Round semantics: what survives a round boundary

When a new user message starts a **round**, MeshWeaver rebuilds the conversation the model sees from the thread's stored **message texts only** — the ordered user and assistant cell bodies (`ThreadMessage.Text`). Tool calls and tool results from the previous round are **not** replayed. (This is `LoadFullConversationHistoryFromMesh` in `ThreadExecution`: it walks the thread's `Messages`, reads each cell, and reconstructs `ChatMessage(role, text)` — nothing else.)

Two consequences drive the whole design:

- **Heavy tool results die at the round boundary — and that is a feature.** End the reading phase with a round boundary and the document simply stops being in context. You get the "document absent" state from §1 for free.
- **Anything the next round needs must be written into the assistant's answer *text*.** State that lived only in a tool result — including facts the agent discovered and acted on — is gone after the boundary. If the next round depends on it, the assistant must emit it as part of its reply.

Also worth knowing when you reason about cost: token usage is stamped when a round **terminates**. A round that dies mid-flight (an infrastructure error, a cancellation) may record **no usage at all**, so thread-level cost figures are a *floor*, not an exact total (see GitHub issue #369).

## 3. The two-phase extract-then-write protocol

For any extraction-shaped task, split the work across a round boundary:

1. **Phase 1 — read.** The agent reads the source(s) and ends by emitting a **complete, self-sufficient worksheet** as its answer text. The user reviews it and replies to continue.
2. **Phase 2 — write.** A fresh round creates all nodes strictly from the worksheet.

This buys three things at once:

- **The document is billed only during phase 1.** Phase 2's context is the worksheet, not the source (§1, §2).
- **The worksheet is a human review gate** before anything is written.
- **The worksheet is durable checkpoint state.** After a crash or an interruption, a resumed round still has it in the conversation.

Corollary — **the worksheet must be genuinely self-sufficient.** The source is unreachable in phase 2, so anything phase 2 needs (including any master data required to create new dimension nodes) has to be *in* the worksheet.

## 4. Hard vs. soft enforcement — affordance beats instruction

The [`AgentConfiguration`](/Doc/Architecture/AgenticAI) surface gives you real, hard levers — use them instead of arguing with the model in the prompt:

- **`Plugins` — the plugin whitelist.** The standard plugins (Chat, Mesh, LayoutArea, Data) are always loaded; `Plugins` adds others by name. Each entry may carry a **per-method filter**: the frontmatter form `PluginName:Method1,Method2` (parsed into `AgentPluginReference.Methods`) exposes only those methods of the plugin. Narrow the surface to exactly what the task needs.
- **`MaxToolCallsPerRound` — the per-round tool-call cap.** Maps onto `FunctionInvokingChatClient.MaximumIterationsPerRequest`. Left unset, the default is high enough for a high-volume agent to issue hundreds of tool calls in one round. Set a small value (e.g. 20–30) to force natural break points — on reaching the cap the framework strips tools on the final iteration so the model returns a graceful answer, and you can invite the user to reply "continue" for the next batch.

The design rule, learned the hard way:

> **When an agent repeatedly takes a forbidden action, remove or restrict the enabling tool — do not escalate the prompt wording.**

In the extraction case the model ignored three successive prompt-level prohibitions of a tool-usage pattern, each stronger and closer to the action; removing the enabling plugin from the whitelist ended it *instantly*. Prompt text competes with tool affordances and loses. And keep the two consistent: a prompt that describes a tool surface the agent doesn't actually have (or omits one it does) invites unpredictable behaviour.

## 5. Writing instructions that survive rounds and failures

- **Scope rules to their phase.** "Load the document exactly once" reads differently after a reset — the new round has no memory of the load, so the model reads it as "not yet done" and loads again. Bind such a rule explicitly to the phase it belongs to (§2, §3).
- **Put the rule at the action site.** A path-derivation rule buried in a general section lost to the affordance of a search result three runs in a row; the same rule as the *first sentence of the step that performs the action* — plus a recovery clause for the observed failure signature ("if a `Get` returned only metadata you fetched the wrong node; correct the path before doing anything else") — held. See the [ContentChunkNavigation](/Doc/Architecture/ContentChunkNavigation) note on file-path vs. `Document`-node reads for exactly this failure.
- **Make batching countable.** "Batch your calls" is ignored; "every response in this step MUST contain at least 10 parallel create calls; a response with exactly one is a protocol violation" produced real batches. Expect a compliance ceiling even so — treat instruction-level batching as an optimisation, not a guarantee.
- **Allow a stale-state exception only where it matters.** If a checkpoint artifact can be stale (a crash between phases), permit exactly one re-verification — existence of the dimension nodes immediately before creating them — and keep everything else bound to the checkpoint.

## 6. Write-phase patterns for fact/dimension models

- **Deterministic ids** (`{shortKey}-{period}-{position}`) make interrupted runs resumable: a re-create collides with the existing node instead of duplicating it.
- **The dimension node's id *is* the short key** used in fact ids and reference paths — never an invented long name. Two spellings of the same dimension split the dataset silently: facts referencing the wrong one vanish from every view keyed on the right one.
- **Check existence by listing, not by name-substring.** Dimension counts are small — list them all in one search and match on key *and* display name. A substring search on a name variant returns a false negative and the agent then creates a duplicate.
- **Verify with a count-check per created type plus one read-back spot check per type** — not a read per node.
- **Do not verify through the search index immediately after writing.** The index is eventually consistent ([CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess)): a count-search issued right after a batch of creates can return zero, and an agent then silently *downgrades* its verification instead of failing it. With deterministic ids the robust check is a direct `Get` on the expected paths; otherwise defer the count or reconcile against the checkpoint.
- **Reconcile writes against the checkpoint.** The closing summary should enumerate which worksheet rows were intentionally *not* written and why (e.g. values the source doesn't report). An unexplained delta between "rows in the worksheet" and "nodes created" is the signature of silent under-creation.

See also the typed-content discipline in [MeshPlugin → Create](/Doc/AI/Tools/MeshPlugin): `content` field names must match the registered type exactly, or the unknown fields are silently dropped and the node renders empty.

## 7. Operational notes

- **Measuring true cost.** Sum every `_Usage` satellite across the whole thread tree — delegation sub-threads record separately (and may be priced as an unknown model, GitHub issue #369), and error-terminated rounds may be missing entirely (§2).
- **Filename hygiene.** Until path canonicalisation ships (GitHub issue #364), prefer ASCII filenames for uploads: decomposed-Unicode names index fine but fail by-name reads.

## Measured effect

The same task shape and model class, tuned across the patterns above:

| Configuration | Cost per report |
|---|---|
| Naive: single-phase, per-value reads, per-node creates | $14–23, frequent failures |
| Two-phase protocol, but document re-read in phase 2 + sequential creates | ~$13.5 |
| + full-text read via one `Get` (transformers) + chunk tools removed + batched creates | **$1.9–2.0** per clean run (~$4 when a round was interrupted and redone) |

The numbers are illustrative of one task, not a benchmark — but the *shape* is the point: the big wins came from cutting turns and evicting the document from the write phase (§1–§3), and the last mile from hard enforcement and typed-write discipline (§4, §6).
