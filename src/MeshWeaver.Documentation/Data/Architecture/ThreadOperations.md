---
Name: Thread Operations
Description: "Canonical IMessageHub extension surface for creating, submitting, resubmitting, and managing AI thread messages via reactive stream.Update writes."
---

# Thread Operations

Every thread mutation in MeshWeaver — creating a thread, submitting a message, resubmitting, deleting, marking done, or recording a failure — is handled by extension methods on `IMessageHub` defined in `src/MeshWeaver.AI/HubThreadExtensions.cs`. Tests, GUI, and agents all call these methods. **There is no other public entry point.**

## Why a single surface?

Before this consolidation, tests hand-rolled `new SubmitContext { … }` bags while GUI code called the same `ThreadSubmission.Submit` static — but each callsite chose its own field combination, so the test surface silently drifted from what the GUI actually did. Three design principles drove the unification:

| Principle | What it gives you |
|---|---|
| **Single source of truth** | Tests and the chat view route through identical code. A passing test means the GUI works. |
| **Reactive, not request/response** | All mutations write the thread node via `workspace.GetMeshNodeStream(threadPath).Update(…)`. The per-thread submission watcher reacts to state changes — no `SubmitMessageRequest / Response`, no completion callbacks via `hub.Set<Action<…>>`, no bespoke `IRequest/IResponse` pairs. |
| **Discoverable** | Type `hub.` and IntelliSense lists the full surface. No need to know `ThreadSubmission` exists. |

## The extension surface

```csharp
using MeshWeaver.AI;

// 1. New thread. Creates the thread node via CreateNodeRequest (sanctioned for
//    node-lifecycle) pre-seeded with the first user message. The watcher
//    dispatches the first round as soon as the thread hub activates.
hub.StartThread(
    namespacePath: "ACME/Threads",
    userText: "Help me draft a Q3 roadmap.",
    agentName: "Coder",
    contextPath: "ACME/Roadmap",
    onCreated: node => Navigate($"/{node.Path}"),
    onError: msg => ShowToast(msg));

// 2. Submit into an existing thread. Writes PendingUserMessages on the thread
//    node; the watcher drains the queue into a new round.
hub.SubmitMessage(
    threadPath: "ACME/Threads/q3-roadmap",
    userText: "Add an item about the API redesign.",
    contextPath: "ACME/Roadmap");

// 3. Resubmit (truncate after a user message and re-queue it).
hub.ResubmitMessage(
    threadPath: "ACME/Threads/q3-roadmap",
    userMessageId: "abc12345",
    newUserText: "Add an item about the API redesign — focus on auth.");

// 4. Truncate Messages at the given message id (the watcher reconciles
//    IngestedMessageIds and rewrites the response cells).
hub.DeleteFromMessage(threadPath, atMessageId);

// 5. Mark the thread terminal (Done) or re-open it (Idle). Refuses to act
//    while a round is in flight — the CAS check lives in the Update lambda.
hub.MarkThreadDone(threadPath, done: true);

// 6. Record a one-shot submission failure. The watcher materialises an error
//    cell from the pending entry on the thread node.
hub.RecordSubmissionFailure(
    threadPath, userMessageId, userText, errorMessage);
```

Internally, every method calls `hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(…)`, which auto-routes based on the caller's identity:

- **Same hub as the thread**: the write goes through its local data source directly.
- **Any other hub**: the write routes via the process-wide `IMeshNodeStreamCache` as an RFC-7396 JSON-merge patch. The thread hub's single-threaded action block serialises every mirror's write — no races, no field clobbering.

## Observing the result

The mutation methods are fire-and-forget (`void`). Callers observe state by subscribing to the thread node's remote stream — the same stream the chat view binds to:

```csharp
var thread = workspace.GetMeshNodeStream(threadPath)
    .Select(n => n.Content as MeshThread)
    .Where(t => t != null)
    .Select(t => t!);

var sub = thread
    .Where(t => !t.IsExecuting && t.Messages.Count > baseline)
    .Select(t => t.Messages[^1])
    .Take(1)
    .Subscribe(
        responseId => Logger.LogInformation("Round finished, response {Id}", responseId),
        ex => Logger.LogWarning(ex, "Thread stream errored for {Path}", threadPath));
// Caller owns `sub` and disposes when the wait is no longer relevant.
```

> **100% reactive end-to-end.** No `FirstAsync().ToTask(ct)`, no `await`, no `Task<T>` boundary in application code. The UI re-renders when the stream ticks; a worker waiting for a round chains via `SelectMany`. See [AsynchronousCalls](AsynchronousCalls) → "Why `await` Deadlocks in Hub Handlers".

Tests bridge to `Task` exactly once at the assertion edge — see [WritingTests](WritingTests). `ThreadFlow.SubmitAndWait` packages submit + wait into one observable for that test-edge use.

## One-shot callbacks on `StartThread`

`onCreated` fires exactly once when the new thread node is confirmed (used by the chat view to navigate to the new thread). `onError` fires exactly once if create or submit fails (post returned null, permission denied, etc.). Both parameters are optional — pass `null` if you don't need them.

> These callbacks are for **signalling** (a one-shot transition), not for **observation** (continuous state). Anything that wants continuous state subscribes to the thread node's remote stream.

## What the watcher does

When `Content.PendingUserMessages` becomes non-empty AND `Status` is `Idle` or `Cancelled` (a stopped round re-dispatches like `Idle`), the submission watcher — installed via `ThreadSubmission.InstallServerWatcher` during thread hub initialization — takes over:

1. Drains `PendingUserMessages` into `Messages` (one round per dispatch, matching Claude Code's turn structure).
2. Allocates the response cell node.
3. Flips `Status = Executing` (so `IsExecuting` becomes true).
4. Invokes `ThreadExecution.ExecuteMessageAsync(execHub, RoundParams, AccessContext?)` **directly as a method** — no message dispatch.

`Resubmit`, `DeleteFromMessage`, and `RecordSubmissionFailure` each perform their full thread-state mutation **inline** inside the `GetMeshNodeStream(threadPath).Update(…)` lambda of the corresponding hub extension method. There are no intent fields, no per-operation watchers — only the single submission watcher remains. `MarkThreadDone` likewise writes `Status` directly. The earlier intent-payload records (`ResubmitIntent`, `FailureRecord`) and their matching thread-node fields (`RequestedResubmit`, `RequestedDeleteFromMessageId`, `PendingFailures`) were deleted on 2026-05-27.

### Status state machine

`ThreadExecutionStatus` follows a well-defined lifecycle:

```
Idle ──► StartingExecution ──► Executing ──► Idle
                                    │
                                    └──► Cancelled
```

Key properties:

- There is **no** transient `Completing` status — terminal writes are atomic.
- `Cancelled` is a distinct, visible terminal status that re-dispatches like `Idle` when new input is queued.
- Cancellation is requested by setting `RequestedStatus = Cancelled` (GUI Stop button, or a parent cancelling a sub-thread). The cancel watcher cancels the CTS; the streaming loop's terminal write flips `Status → Cancelled` and clears `RequestedStatus`.

**Wake-up recovery** (`InitializeThreadLifecycle`): on hub activation the thread reads its own node's first stream emission and drives any non-terminal state to valid once — a pending `RequestedStatus = Cancelled` is honoured, an interrupted `Executing` round **resumes its existing response cell** (re-entering `StartingExecution`; `DispatchAfterClaim` reuses `ActiveMessageId`), and `Idle` / `Cancelled` with pending input is left for the submission watcher. See [ActivityControlPlane](ActivityControlPlane) → "Wake-up recovery".

### Mid-execution inbox drain (A7)

While `Executing`, the `check_inbox` tool drains queued user messages. If a drain happens mid-stream it performs a **clean output-cell transition**: it freezes the current response cell (`Completed`), places the new user cells after it, and switches streaming to a fresh response cell:

```
[R1 completed] → [U…] → [R2 streaming]
```

The streaming writer targets a per-round `ActiveResponseSegment` whose `ResponseMsgId` / `TextBaseline` the tool re-points, so the continuation streams into R2. A stale buffered push slices off the baseline to empty — harmless. An empty drain leaves the cell unchanged.

## Internal helpers — do not call directly

`ThreadSubmission.InstallServerWatcher`, `PlanNextRound`, `FindUnprocessedUserMessages`, and the `ThreadExecution.*` server-side helpers are **internal** to `MeshWeaver.AI`. They implement the watcher; they are not called by application code. If you need a thread mutation that isn't on the `IMessageHub` surface, extend the surface — don't reach into the internals.

### Migrating from the deleted API

The legacy `SubmitContext` / `ResubmitContext` parameter-bag records and the old `ThreadSubmission.Submit` / `CreateThreadAndSubmit` / `Resubmit` / `ApplyResubmit` / `ApplyDeleteFromMessage` / `MarkThreadDone` / `ApplyRecordSubmissionFailure` static methods were deleted on 2026-05-27.

> Build errors of the form `'ThreadSubmission' does not contain a definition for 'Submit'` mean the callsite has not been migrated yet. Replace it with the corresponding `hub.X(…)` extension listed in [The extension surface](#the-extension-surface) above.

## See also

- [RequestViaStreamUpdate](RequestViaStreamUpdate) — the canonical "stream.Update + watcher" pattern this surface is built on
- [ActivityControlPlane](ActivityControlPlane) — the `Status` / `RequestedStatus` pattern thread state uses
- [AsynchronousCalls](AsynchronousCalls) — why everything returns `IObservable<T>` and how tests bridge to `Task`
