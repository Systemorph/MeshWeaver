---
Name: Thread Operations
Description: "Canonical IMessageHub extension surface for creating, submitting, resubmitting, and managing AI thread messages via reactive stream.Update writes."
---

# Thread Operations

Every thread mutation in MeshWeaver — creating a thread, submitting a message, resubmitting, deleting, marking done, or recording a failure — is handled by extension methods on `IMessageHub` defined in `src/MeshWeaver.AI/HubThreadExtensions.cs`. Tests, GUI, and agents all call these methods. **There is no other public entry point.**

<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#1e88e5"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#43a047"/>
    </marker>
  </defs>
  <rect width="760" height="300" rx="12" fill="#1a1a2e" opacity="0.55"/>
  <rect x="20" y="28" width="110" height="44" rx="8" fill="#5c6bc0"/>
  <text x="75" y="46" text-anchor="middle" fill="#fff" font-weight="bold">hub.</text>
  <text x="75" y="63" text-anchor="middle" fill="#fff">SubmitMessage</text>
  <rect x="20" y="108" width="110" height="44" rx="8" fill="#5c6bc0"/>
  <text x="75" y="126" text-anchor="middle" fill="#fff" font-weight="bold">hub.</text>
  <text x="75" y="143" text-anchor="middle" fill="#fff">StartThread</text>
  <rect x="20" y="188" width="110" height="44" rx="8" fill="#5c6bc0"/>
  <text x="75" y="206" text-anchor="middle" fill="#fff" font-weight="bold">hub.</text>
  <text x="75" y="223" text-anchor="middle" fill="#fff">ResubmitMessage</text>
  <text x="75" y="270" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">IMessageHub extensions</text>
  <line x1="130" y1="50" x2="188" y2="125" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="130" y1="130" x2="188" y2="133" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="130" y1="210" x2="188" y2="143" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="193" y="105" width="150" height="54" rx="10" fill="#26a69a"/>
  <text x="268" y="126" text-anchor="middle" fill="#fff" font-weight="bold">stream.Update()</text>
  <text x="268" y="143" text-anchor="middle" fill="#fff" font-size="11">PendingUserMessages</text>
  <text x="268" y="157" text-anchor="middle" fill="#fff" font-size="11">on MeshThread node</text>
  <line x1="343" y1="132" x2="400" y2="132" stroke="#1e88e5" stroke-width="2" marker-end="url(#arr-blue)"/>
  <text x="371" y="122" text-anchor="middle" fill="#1e88e5" font-size="11">reacts</text>
  <rect x="405" y="105" width="140" height="54" rx="10" fill="#f57c00"/>
  <text x="475" y="126" text-anchor="middle" fill="#fff" font-weight="bold">Submission</text>
  <text x="475" y="143" text-anchor="middle" fill="#fff" font-weight="bold">Watcher</text>
  <text x="475" y="158" text-anchor="middle" fill="#fff" font-size="11">drains queue → Executing</text>
  <line x1="545" y1="132" x2="600" y2="132" stroke="#43a047" stroke-width="2" marker-end="url(#arr-green)"/>
  <text x="572" y="122" text-anchor="middle" fill="#43a047" font-size="11">invokes</text>
  <rect x="605" y="105" width="135" height="54" rx="10" fill="#1e88e5"/>
  <text x="672" y="126" text-anchor="middle" fill="#fff" font-weight="bold">ThreadExecution</text>
  <text x="672" y="143" text-anchor="middle" fill="#fff" font-size="11">.ExecuteMessageAsync</text>
  <text x="672" y="158" text-anchor="middle" fill="#fff" font-size="11">streams response cell</text>
  <line x1="672" y1="159" x2="672" y2="218" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="605" y="222" width="135" height="40" rx="8" fill="#43a047"/>
  <text x="672" y="239" text-anchor="middle" fill="#fff" font-weight="bold">Status → Idle</text>
  <text x="672" y="254" text-anchor="middle" fill="#fff" font-size="11">observable ticks</text>
  <line x1="605" y1="242" x2="348" y2="242" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="5,4" marker-end="url(#arr)"/>
  <text x="476" y="237" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">GetMeshNodeStream fires</text>
  <rect x="193" y="218" width="150" height="40" rx="8" fill="#8e24aa"/>
  <text x="268" y="235" text-anchor="middle" fill="#fff" font-weight="bold">Observers notified</text>
  <text x="268" y="251" text-anchor="middle" fill="#fff" font-size="11">GUI · tests · agents</text>
</svg>

*Thread lifecycle: hub extension methods write `PendingUserMessages` via `stream.Update`; the submission watcher reacts, runs the execution, and notifies all observers when done.*

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

> **100% reactive end-to-end.** No `FirstAsync().ToTask(ct)`, no `await`, no `Task<T>` boundary in application code. The UI re-renders when the stream ticks; a worker waiting for a round chains via `SelectMany`. See [AsynchronousCalls](/Doc/Architecture/AsynchronousCalls) → "Why `await` Deadlocks in Hub Handlers".

Tests bridge to `Task` exactly once at the assertion edge — see [WritingTests](/Doc/Architecture/WritingTests). `ThreadFlow.SubmitAndWait` packages submit + wait into one observable for that test-edge use.

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

**Wake-up recovery** (`InitializeThreadLifecycle`): on hub activation the thread reads its own node's first stream emission and drives any non-terminal state to valid once — a pending `RequestedStatus = Cancelled` is honoured, an interrupted `Executing` round **resumes its existing response cell** (re-entering `StartingExecution`; `DispatchAfterClaim` reuses `ActiveMessageId`), and `Idle` / `Cancelled` with pending input is left for the submission watcher. See [ActivityControlPlane](/Doc/Architecture/ActivityControlPlane) → "Wake-up recovery".

### Mid-execution inbox drain (A7)

While `Executing`, the `check_inbox` tool drains queued user messages. If a drain happens mid-stream it performs a **clean output-cell transition**: it freezes the current response cell (`Completed`), places the new user cells after it, and switches streaming to a fresh response cell:

```
[R1 completed] → [U…] → [R2 streaming]
```

The streaming writer targets a per-round `ActiveResponseSegment` whose `ResponseMsgId` / `TextBaseline` the tool re-points, so the continuation streams into R2. A stale buffered push slices off the baseline to empty — harmless. An empty drain leaves the cell unchanged.

## Internal helpers — do not call directly

`ThreadSubmission.InstallServerWatcher`, `PlanNextRound`, `FindUnprocessedUserMessages`, and the `ThreadExecution.*` server-side helpers are **internal** to `MeshWeaver.AI`. They implement the watcher; they are not called by application code. If you need a thread mutation that isn't on the `IMessageHub` surface, extend the surface — don't reach into the internals.

### Migrating from the deleted API

The `IMessageHub` extensions above are the complete submission surface — there is no other entry point.

> Build errors of the form `'ThreadSubmission' does not contain a definition for 'Submit'` mean the callsite has not been migrated yet. Replace it with the corresponding `hub.X(…)` extension listed in [The extension surface](#the-extension-surface) above.

## Resurrection on activation — the thread must self-heal

A thread hub can activate onto a node a previous process left **mid-round**:
`Status = Executing`, an `ActiveMessageId` whose `Task.Run` is gone, maybe an
unfinished `delegate_to_agent` tool call pointing at a child sub-thread. The
portal restarted, an Orleans grain deactivated and came back, or a test seeded the
post-crash shape straight into storage. `ThreadExecution.InitializeThreadLifecycle`
is the recovery: on activation it reads the OWN node's loaded state and drives any
non-terminal state to a valid one.

The non-negotiable property is **self-healing** — recovery reaches a terminal/valid
state with no external nudge, and a single missed observation must not strand the
thread forever:

- **Never give up on the loaded-state read.** Recovery waits for the first real
  thread emission and **re-establishes** the observation if it faults before
  acting — it does NOT `Take(1).Timeout(15s)` and silently abandon the thread when
  that emission is dropped/late under load. The one-shot give-up *was* the
  sub-thread cold-load "deadlock" (really a missed observation).
- **`Executing` + `ActiveMessageId`** → resume the same response cell (re-enter
  `StartingExecution`; the `_Exec` round watcher re-dispatches). Resume → the round
  naturally finishes.
- **`Executing` mid-delegation** → re-observe the existing child sub-thread (do not
  re-run the agent loop — that re-delegates). When the child reaches terminal,
  write its result back so the parent settles/continues.
- **Guarantee terminal.** A last-resort watchdog forces a wedged round to `Idle`
  after a generous grace of *no node progress* (`Throttle` resets on every
  emission, so live streaming never trips it; threads waiting on a child are
  skipped — the heartbeat ticker owns that staleness).

Every child sub-thread runs the same recovery recursively, so a parent's
re-observation is guaranteed to fire. See
[DebuggingMessageFlow → resurrection on init](/Doc/Architecture/DebuggingMessageFlow) for the trace
signature: continuous work then silence = missed observation, not a lock.

## Harness, agent, and model selection

The chat composer's **single top-level choice is the harness** — the execution
environment a round runs under. There are three (`MeshWeaver.AI.Harnesses`):

| Harness | Constant | Resolves to |
|---|---|---|
| `MeshWeaver` | `Harnesses.MeshWeaver` | the user's chosen agent + model (the agent/model selectors are shown) |
| `Claude Code` | `Harnesses.ClaudeCode` | the single built-in agent whose `GroupName == "Claude Code"` |
| `GitHub Copilot` | `Harnesses.Copilot` | the single built-in agent whose `GroupName == "GitHub Copilot"` |

"Harness" is **not a new execution concept** — it is a grouping over the existing
agent selection (`AgentConfiguration.GroupName`). Picking a non-MeshWeaver harness
resolves to that group's one agent and hides the agent/model dropdowns; picking
MeshWeaver reveals them so the user can choose among the MeshWeaver-group agents +
models. Routing to the concrete `IChatClientFactory` is unchanged — it still keys
off the resolved agent's `PreferredModel`.

Selection has **one home — the composer** (`Thread.Composer`, a `ThreadComposer`): the
data-bound `Harness` / `AgentName` / `ModelName` fields the in-thread selectors bind to.
`StartThread` / `SubmitMessage` keep that composer current (seeding it / folding the
submitted message's selection back into it). When the submission watcher dispatches a round
it reads the sticky selection straight off `Thread.Composer` — `PlanNextRound` →
`RoundDispatch` → `RoundParams.Harness`/`.AgentName`/`.ModelName` — and `ThreadExecution`
stamps the **assistant cell** with what actually ran (`ThreadMessage.Harness` etc., a display
record, never the source). There is **no thread-level selection mirror** (`Pending*`,
`SelectedAgentName`/`SelectedModelName`/`SelectedHarness`, `DraftText` were removed — they
duplicated the composer and drifted).

The **output cell records what actually ran**: `ThreadExecution` captures the
harness, the real model id the harness reports (`ChatResponseUpdate.ModelId` — e.g.
Claude Code resolving `sonnet` to a concrete id), and the token usage
(`UsageContent`). The chat renders one muted line per assistant cell:
`Harness · HH:mm:ss · duration · N in / M out` (model dropped from the line; still
stored on the cell).

## The chat template (`{userHome}/_ThreadTemplate`)

The composer's in-progress **draft text + harness/agent/model selection persist
server-side** on a single stable per-user node at `{userHome}/_ThreadTemplate`
(`userHome == AccessContext.ObjectId`). There is **no browser localStorage** — the
template node is the source of truth, so the draft and selection survive a reload /
reboot and are shared across every space the user composes in.

The composer state is a single record — `ThreadComposer` — with two homes (the
out-of-thread template node, and `Thread.Composer` embedded on a real thread). It carries
the draft `MessageContent`, the sticky `Harness`/`AgentName`/`ModelName` selection, and the
per-message `Attachments`/`ContextPath`. There is **no separate `DraftText`/`Selected*`
mirror on the thread** — the composer is the only selection/draft state.

- **Load**: the composer one-shot-reads the template on init and applies the saved
  selection (always — the picker default) and, in the new-thread composer only, the
  draft text into the Monaco editor.
- **Save**: selection changes write through the composer immediately; the draft text is
  debounced (`Throttle(600ms)`). In an *existing* thread the same `ThreadComposer` lives
  embedded as `Thread.Composer`, so a `/agent` / `/model` / `/harness` pick updates it in
  place — and is only *accepted* into a round when the thread is idle (the submission
  watcher's `PlanNextRound` runs only on `Idle`/`Cancelled`, so a mid-round change stays
  queued until the next round).
- **Submit**: `StartThread` copies the template's composer (selection) onto the new thread
  as `Thread.Composer` (the typed text becomes the first message; the draft + attachments
  empty, the selection stays), so the next new thread inherits the selection.

The template is **inert**: it carries a composer draft but never any `PendingUserMessages`,
so the submission watcher never fires on it. Its namespace is `{userHome}` (not
`{userHome}/_Thread`), so the namespace-scoped resume-thread query
(`namespace:{ns}/_Thread`) never lists it.

## Read-only threads (owner-only edit)

**A thread is editable only by its owner.** When the chat view binds a thread whose
`MeshNode.CreatedBy` (surfaced as `ThreadViewModel.CreatedBy`) differs from the
current user, it renders **read-only**: the input footer, the Stop button, and the
per-message edit / resubmit / delete actions are all hidden. The new-thread composer
(no `threadPath`) and the user's own threads stay fully editable. This is a UI
affordance on top of server-side access control — not a replacement for it.

## Thread identity — the owner is the standing access context

Everything the thread hub does with no live caller (the submission watcher's claim write, the
round dispatch, the data-source sync propagation) runs under the **thread owner** — the node's
`CreatedBy`, established on the hub and carried forward via `CircuitContext`. This is what keeps a
cold-start submit (grains inactive, the user's write racing the hub's activation) from posting a
null `AccessContext` that the never-null guard would fail closed. See
[Owner Injection](/Doc/Architecture/OwnerInjection) for the rule and the cold-start race it fixes.

## See also

- [Owner Injection](/Doc/Architecture/OwnerInjection) — the thread/activity owner as standing access identity, carried forward via `CircuitContext`
- [RequestViaStreamUpdate](/Doc/Architecture/RequestViaStreamUpdate) — the canonical "stream.Update + watcher" pattern this surface is built on
- [ActivityControlPlane](/Doc/Architecture/ActivityControlPlane) — the `Status` / `RequestedStatus` pattern thread state uses, and its matching recovery-on-init
- [AsynchronousCalls](/Doc/Architecture/AsynchronousCalls) — why everything returns `IObservable<T>` and how tests bridge to `Task`
- [DebuggingMessageFlow](/Doc/Architecture/DebuggingMessageFlow) — diagnosing a hang that is really a missed observation
