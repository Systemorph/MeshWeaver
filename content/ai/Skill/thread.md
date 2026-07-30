---
nodeType: Skill
name: /thread
description: Manage conversation threads — read a thread's real status, mark threads Done (one or in bulk), and use the one submission surface. A thread's lifecycle lives on content.status (Idle | StartingExecution | Executing | Cancelled | Done) — managing threads is one search plus one patch per thread, never schema archaeology.
icon: 🧵
category: Skills
order: 14
---

You are managing **conversation threads**. Everything you need is on the thread node's `content` —
do not probe schemas, do not inspect sibling threads one by one, do not delegate one-line patches
to a worker. This page IS the schema summary.

# 1. What a Thread node is

A thread lives at `{owner}/_Thread/{id}` with `nodeType: "Thread"`. The content fields that matter
for management:

| Field | Meaning |
|---|---|
| `status` | The lifecycle: `Idle`, `StartingExecution`, `Executing`, `Cancelled`, `Done`. **This is the only "done" flag.** The MeshNode-level `state` (`Active`, …) is node lifecycle, not thread lifecycle — never touch it. |
| `messages` | Ids of the conversation cells (ingested user messages + assistant response cells). Each id is a child node `{threadPath}/{messageId}` of `nodeType: ThreadMessage`. Owned by the execution watcher — never edit by hand. |
| `pendingUserMessages` | The inbox: submitted-but-not-yet-ingested user messages. The watcher drains it at round boundaries — never clear it by hand. |
| `userMessageIds` / `ingestedMessageIds` | Submission bookkeeping, also watcher-owned. |
| `summary`, `lastActivityAt` | Last round's summary; last execution heartbeat. |
| `composer` | The thread's defaults (harness, agent, model, context path). |

Satellites under a thread: `{threadPath}/{messageId}` message cells, `_Usage/*` (TokenUsage),
`_Notification/*` (the completion bell), and delegation sub-threads nested under a response cell.

# 2. Mark a thread Done — one patch

```text
patch @{owner}/_Thread/{id}   { "content": { "status": "Done" } }
```

That is the whole operation. It is **idempotent** — patching an already-Done thread is harmless, so
you do not need to read each thread's status first.

From code, the same operation is `hub.MarkThreadDone(threadPath, done)` — part of the one thread
surface in `HubThreadExtensions` (`StartThread`, `SubmitMessage`, `ResubmitMessage`,
`DeleteFromMessage`, `MarkThreadDone`, `RecordSubmissionFailure`). Never invent a request type
(`SetThreadStatusRequest` and friends do not exist and must not be created).

# 3. Bulk housekeeping ("mark all done except …")

1. **List once:** `search nodeType:Thread namespace:{owner}/_Thread scope:children sort:lastModified-desc`
2. **Pick the keep-set from that listing** (e.g. the top N rows).
3. **Patch every other row** with `{ "content": { "status": "Done" } }` — idempotent, so no
   per-thread `get`, no status pre-check, no worker delegation. N threads = 1 search + ≤N patches.

Two exclusions to respect:

- **Never mark the thread you are currently executing in.** Its status belongs to the running
  round's watcher; it flips to a terminal state when the round ends.
- Delegation sub-threads live *under* their parent's response cell and end with their parent —
  `scope:children` on `{owner}/_Thread` already excludes them, so don't go hunting with
  `scope:descendants`.

# 4. Reading a thread's real state

`get @{threadPath}` is live and authoritative. Interpreting what you see:

- `status: Done` / `Cancelled` / `Idle` — terminal or at rest; safe to patch.
- `status: StartingExecution` / `Executing` with a **recent** `lastActivityAt` — a round is running;
  leave it alone.
- `status: StartingExecution` / `Executing` with `lastActivityAt` far in the past and pending
  messages that never drain — the thread is **wedged**. That is a framework bug, not a state you
  fix by patching: report it (thread path + timestamps) instead of fighting the watcher, which owns
  that status.

# 5. Anti-patterns (each of these has burned a real session)

- Fetching `<nodeType>/schema`, probing sibling threads one at a time, or "checking how Done looks
  elsewhere" — the schema is §1.
- Delegating a batch of one-line patches to a Worker agent — just issue the patches.
- Setting the MeshNode-level `state`, hand-editing `messages`/`pendingUserMessages`, or inventing a
  request/response type for a status flip — the status patch (§2) is the entire surface.
