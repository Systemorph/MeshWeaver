# Thread Operations — the canonical `IWorkspace` surface

Every thread mutation in MeshWeaver — creating a thread, submitting a message, resubmitting, deleting from a message, marking done, recording a failure — goes through extension methods on `IMessageHub` defined in `src/MeshWeaver.AI/HubThreadExtensions.cs`. **Tests, GUI, and agents all call these.** There is no other public entry point.

## Why

Three reasons:

1. **Single source of truth.** Before this consolidation, tests hand-rolled `new SubmitContext { … }` bags and GUI code called the same `ThreadSubmission.Submit` static — but each callsite chose its own field combination, which meant the test surface drifted from what the GUI actually did. The IWorkspace extensions are the *only* shape; both tests and the chat view route through them, so a passing test means the GUI works.

2. **Reactive, not request/response.** All thread mutations write the thread node via `workspace.GetMeshNodeStream(threadPath).Update(…)`. The per-thread submission watcher (installed by `ThreadSubmission.InstallServerWatcher`) reacts to the resulting state changes. No `SubmitMessageRequest` / `SubmitMessageResponse`, no completion callbacks via `hub.Set<Action<…>>`, no bespoke `IRequest`/`IResponse` pairs.

3. **Discoverable.** `workspace.` + IntelliSense lists the full surface. No need to know `ThreadSubmission` exists.

## The surface

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

The extensions are on `IMessageHub` — every caller already holds one (`Hub`, `client`, `hub`). Internally each method goes through `hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(…)`, which auto-routes:

- If the writer is the thread hub itself, the write goes through its local data source.
- If the writer is anywhere else, the write routes via the process-wide `IMeshNodeStreamCache` as an RFC-7396 JSON-merge patch. The thread hub's single-threaded action block serialises every mirror's write — no races.

## Observing the result

The mutation methods are `void` / fire-and-forget. Callers observe state by subscribing to the thread node's remote stream — the same stream the chat view binds to:

```csharp
var thread = workspace.GetMeshNodeStream(threadPath)
    .Select(n => n.Content as MeshThread)
    .Where(t => t != null)
    .Select(t => t!);

// Wait for the next round to complete.
var responseId = await thread
    .Where(t => !t.IsExecuting && t.Messages.Count > baseline)
    .Select(t => t.Messages[^1])
    .Take(1).Timeout(TimeSpan.FromSeconds(30))
    .FirstAsync().ToTask(ct);
```

`ThreadFlow.SubmitAndWait` packages this exact pattern as a one-liner for tests:

```csharp
var responseId = await ThreadFlow.SubmitAndWait(client, threadPath, "ping")
    .FirstAsync().ToTask(ct);
```

## The one-shot callbacks

`StartThread.onCreated` fires exactly once when the new thread node is confirmed (used by the chat view to navigate). `onError` fires exactly once if the create / submit fails (post returned null, permission denied, etc.). Both are optional; pass `null` if you don't need them.

These callbacks exist for *signalling* (a one-shot transition), not for *observation* (continuous state). Anything that wants continuous state subscribes to the thread node's remote stream.

## What the watcher does

When the thread node's `Content.PendingUserMessages` becomes non-empty AND `IsExecuting` is false, the submission watcher (installed via `ThreadSubmission.InstallServerWatcher` during thread hub initialization):

1. Drains `PendingUserMessages` into `Messages` (one round per dispatch — Claude-Code-style turn structure).
2. Allocates the response cell node.
3. Flips `IsExecuting = true`, `Status = Executing`.
4. Invokes `ThreadExecution.ExecuteMessageAsync(execHub, RoundParams, AccessContext?)` **directly as a method** — no message dispatch.

Resubmit / DeleteFromMessage / RecordSubmissionFailure each do the full thread-state mutation **inline** inside their hub extension method's `GetMeshNodeStream(threadPath).Update(...)` lambda. No intent fields, no per-operation watcher — only the submission watcher remains. `MarkThreadDone` likewise writes `Status` directly. Earlier intent-payload records (`ResubmitIntent`, `FailureRecord`) and matching thread-node fields (`RequestedResubmit`, `RequestedDeleteFromMessageId`, `PendingFailures`) were deleted (2026-05-27).

## What about `ThreadSubmission` and `ThreadExecution`?

`ThreadSubmission.InstallServerWatcher`, `PlanNextRound`, `FindUnprocessedUserMessages` and the `ThreadExecution.*` server-side helpers are **internal** to `MeshWeaver.AI` — they implement the watcher, they aren't called by application code. If you need a thread mutation that isn't on the IWorkspace surface, extend the surface — don't reach into the internals.

The legacy `SubmitContext` / `ResubmitContext` parameter-bag records and the old `ThreadSubmission.Submit` / `CreateThreadAndSubmit` / `Resubmit` / `ApplyResubmit` / `ApplyDeleteFromMessage` / `MarkThreadDone` / `ApplyRecordSubmissionFailure` static methods are deleted (2026-05-27). Build errors of the form `'ThreadSubmission' does not contain a definition for 'Submit'` mean the call hasn't been migrated yet — change it to the corresponding `workspace.X(…)` extension.

## See also

- `Doc/Architecture/RequestViaStreamUpdate.md` — the canonical "stream.Update + watcher" pattern this surface is built on
- `Doc/Architecture/ActivityControlPlane.md` — the Status / RequestedStatus pattern thread state uses
- `Doc/Architecture/AsynchronousCalls.md` — why everything returns `IObservable<T>` and how tests bridge to `Task`
