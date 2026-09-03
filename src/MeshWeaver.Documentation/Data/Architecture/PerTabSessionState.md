---
Name: Per-Tab Session State
Category: Architecture
Description: A mesh node is shared by every tab of one account by construction, so "which page is this viewer on" and "navigate ME there" must never live unaddressed on a per-user node — and a hazard is only a defect once you have found the code that renders it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="9" height="16" rx="1"/><rect x="13" y="4" width="9" height="16" rx="1"/><path d="M6.5 9h0"/><path d="M17.5 9h0"/></svg>
---

# Per-Tab Session State

**A `MeshNode` is shared by every viewer who can read it — including the same person in a second browser tab. So any state whose meaning is "*this viewer, right now, in this window*" is wrong on a node unless it says WHICH viewer it is for, and no amount of care at the call sites can rescue it.** The two questions that always have this shape are:

- **"Which page is the viewer looking at?"** — the navigation context.
- **"Take *me* there."** — a navigation command addressed to one window.

This page came out of [MeshWeaver#3060](https://github.com/Systemorph/MeshWeaver/issues/3060) ("chat session cross-talk when the same account opens a second browser tab"). It carries three things: the scoping rule, the shape that was actually built for it, and a methodological lesson the investigation learned the hard way — **a mechanism you can read in the code is a hypothesis until you have found the code that RENDERS it.** The most incriminating call sites here turned out to sit in a component with no render site, and the first version of this page reported them as live.

## The layering that makes this a trap

Everything *above* the node is already correctly per-tab, which is exactly why the defect is hard to see from the top:

| Layer | Keying | Verdict |
|---|---|---|
| Blazor circuit | one per tab (`Circuit.Id`) | per tab |
| `ICircuitContextAccessor` | `AddScoped` — one instance per circuit | per tab |
| Portal hub | `CreatePortalAddress(circuitContextAccessor.CircuitId)` | per tab |
| `INavigationService`, `SidePanelStateService` | `AddScoped` | per tab |
| Layout-area subscription | keyed `(Subscriber, StreamId)`; the subscriber **is** the portal hub address | per tab |
| Layout-area state (`/data/{id}`, `host.UpdateData`) | lives on that per-subscriber `LayoutAreaHost` | per tab |
| **A mesh node's content** | **the node path** | **shared by everything that can read it** |

The whole stack is per-circuit right down to the last hop — and then the value is written into a node, where the isolation ends. **The node is the only shared thing in the chain, and it is also the only durable one**, which is precisely why per-tab state keeps drifting into it: persistence and isolation pull in opposite directions here.

## The three shapes, and the one framework primitive

> **Ask what the value would mean to a second window of the same account. If the honest answer is "not that", it does not belong *unaddressed* on a node keyed by user.**

- **Per-viewer *preference*** (last-used model, panel position, theme) → a per-user node is right, plain and unaddressed. Two tabs agreeing is the feature.
- **Per-viewer *session* state** (which page, which selection, an in-progress form) → either the **layout area's own store**, which is already per-subscriber (`host.UpdateData(...)` / `/data/{id}`), or, when it genuinely has to persist, a node field that **carries its addressee** and is honoured only for the viewer it names.
- **A command addressed to one window** ("take me there") → **do not put it on a node at all.** Post it, and let the transport carry the addressee.

**The primitive for all three is `LayoutAreaHost.Viewer`** — the area subscription's subscriber, normalized through `Address.Host`, which for a browser tab is `portal/{circuitId}`: exactly one per tab. `LayoutAreaHost.NavigateTo` / `NavigateToSidePanel` already post to that address, so a stamp written from `Viewer` and a command posted by `NavigateTo` name the same tab by construction. (`LayoutAreaViewerTest` pins both halves: an area knows its viewer, and two subscribers to one area get different answers — if they ever agreed, "addressed" would mean nothing.)

And one anti-pattern: **do not "fix" it by minting a node per tab** (`{user}/_Thread/ThreadComposer/{circuitId}`). Circuits are unbounded and short-lived; that trades a correctness bug for unbounded node churn and loses the durability the node was there for. Addressing a field costs one string; a node per circuit costs a node per reload.

## The concrete instance: the out-of-thread chat composer

The "new chat" composer is a **per-user singleton node**:

```csharp
// MeshWeaver.Plugins/src/MeshWeaver.AI/ThreadComposerNodeType.cs
public static string PathFor(string user) => $"{user}/{ThreadNodeType.ThreadPartition}/{NodeType}";
//                                            → {user}/_Thread/ThreadComposer
```

Its content (`ThreadComposer`) mixed three lifetimes in one flat record. Each field now says which it is:

| Field | Scope | How |
|---|---|---|
| `Harness`, `AgentName`, `ModelName`, `Effort` | **per user** | plain — sharing across tabs is the feature |
| `MessageContent`, `Attachments` | **per user, deliberately** | plain — see below |
| `ContextPath`, `ContextReference` | **per tab** | addressed by `ContextAddressee` |
| `OpenThreadPath` | **per tab** | retired — the command is posted, not stored |

### Why the draft stayed per user

`MessageContent` is the one field where both readings are defensible, and the deciding argument is not taste — it is that **a Blazor circuit dies on every page reload.** Keying the draft by circuit would lose it whenever the tab reloads, the network blips, or the server restarts, which is a regression for the overwhelmingly common single-tab case and destroys the exact durability the node exists to provide. So: one person, one draft. What a second tab can do to it — see the draft, and clear it on New Chat — is the ordinary consequence of one shared draft, not a wrong-target write.

### `ContextPath` — addressed, and fails closed

`ContextPath` is not a label. It is the namespace the thread is created in, and then the root the round's write tools resolve a bare argument against:

```
ThreadComposerView.Send
  → ResolveContext(composerPath, storedContextPath, storedAddressee, viewer, user)
  → StartThreadIn → hub.StartThread(namespacePath: …, contextPath: …)
      → the thread NODE at {ns}/_Thread/{speakingId}
      → request.ContextPath → ThreadExecution → client.SetContext(…)
        → AgentChatPaths.ResolveContextPath → MeshPlugin: patch · edit_content · recycle · move · copy
```

So a stored context now wins **only for the viewer `ContextAddressee` names**, and `ResolveContext` is the single place that decides it. The properties that matter:

- **A context addressed to another tab is ignored** — the derivation from the composer's own node path stands ([Plugins#1287](https://github.com/Systemorph/MeshWeaver.Plugins/pull/1287)), so a chat started from a node still carries that node.
- **A context stored with NO addressee is honoured by nobody.** This is the fail-closed half and it is the point: a future writer that forgets to say who it is for produces "my context was not picked up", never "my message was written into someone else's page". Restoring a writer can no longer re-arm the hazard in one commit — an un-addressed writer is inert.
- **A render with no viewer claims nobody's context**, so "unaddressed" and "addressed to nobody in particular" cannot collapse into each other.

On a thread's embedded `Thread.Composer` the same field means something else and needs no addressee: it is the subject *that thread* was started about, fixed at creation. A thread has one context, not one per window — so `StartThread` drops the addressee when it copies the composer onto the thread.

### `OpenThreadPath` — the command left the node entirely

The retired field was the data-bound "navigate here" signal `Send` stamped on the per-user node for a client to observe. That made "take **me** to the thread I just created" a **broadcast**, and a broadcast on shared state is not a command — it is an interrupt for everybody. Two failures, not one: the tab that did not send gets moved, and both tabs race to clear the field, so the tab that *did* send can lose its own navigation to the other tab's clear.

It is now `host.NavigateTo($"/{node.Path}")` in `StartThreadIn`'s `onCreated`. The addressee is the transport's — a `NavigationRequest` posted to the area's own subscriber — so it cannot reach another tab, there is nothing to clear, and there is no race to lose. This also fixes the live one-sided bug that fell out of the same reading ([Plugins#1267](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1267) consequence 1): nothing consumed the field, so a Send from the composer created the thread and left the viewer where they were.

The property is kept, `[Obsolete]`, so content written before the change still deserializes and so `src/` cannot grow a new reader or writer without the compiler saying so. Nothing writes it and nothing reads it.

### What was deleted rather than repaired

`ThreadSidePanelContent`'s `WriteComposerContext` (the only writer `ContextPath` ever had) and its `OpenThreadPath` observer are gone. The component has had no render site since the Plugins#977 module split, so repairing dead code would have been unverifiable; deleting the writer is what makes "restore the side panel" a safe change rather than a re-arming one — and if a writer does come back without an addressee, the read is fail-closed anyway.

## Verifying it — both directions, or it proves nothing

**A suite that only proved isolation would pass a change that broke the sharing**, which is why the committed tests pin both:

| Test | Asserts |
|---|---|
| `PerTabComposerIsolationTest.AModelPickedInOneTab_IsSeenByTheOther` | the per-USER selection still crosses tabs — the feature |
| `PerTabComposerIsolationTest.AContextStoredByOneTab_IsNotTheOtherTabsContext` | both tabs see the shared FIELD (one node — that is the premise), and the DECISION differs: the writer gets its context, the other tab gets its own |
| `PerTabComposerIsolationTest.SendFromOneTab_NavigatesThatTabAndNotTheOther` | the `NavigationRequest` lands in the tab that clicked and nowhere else |
| `ComposerContextTest` | the whole `ResolveContext` decision: addressed-to-me wins, addressed-elsewhere loses, un-addressed is inert, no-viewer claims nothing |
| `LayoutAreaViewerTest` (core) | `LayoutAreaHost.Viewer` names the subscriber, and two subscribers to one area get different answers |

Two independent client hubs stand in for two circuits: each has its own address, its own workspace and its own layout-area subscription — the shape a per-circuit portal hub has.

**And the mutation control, because an assertion that cannot fail is not an assertion.** Reverting each half of the fix independently reddens exactly the tests that cover it, and nothing else:

| mutation | result |
|---|---|
| `ResolveContext` ignores the addressee (a stored context always wins) | `AContextStoredByOneTab_IsNotTheOtherTabsContext` + 5 `ComposerContextTest` rows |
| `StartThreadIn` posts no `NavigationRequest` (the pre-fix stamp-a-field shape) | `SendFromOneTab_NavigatesThatTabAndNotTheOther` |
| both reverted together | **7 fail / 12 pass** — measured 2026-09-03 |
| neither | **19 / 19**, ~4 s |

`AModelPickedInOneTab_IsSeenByTheOther` passes under every mutation, which is the point of listing it: the suite is not being held up by the assertion that would also pass if the fix had quietly made the whole record per-tab.

## The same shape elsewhere

`ThreadComposer` is the instance that produced #3060, but the pattern recurs wherever a singleton is keyed by user identity and holds a live session:

- `MeshWeaver.Plugins/src/MeshWeaver.AI/Connect/ConnectSessionManager.cs` — sessions keyed `{ownerPath}|{provider}`; `StartConnect` cancels the existing slot before overwriting it, so a provider sign-in started in tab B **kills tab A's live CLI session**.
- `MeshWeaver.Blazor.Portal/Resize/DimensionManager.cs` — a process-wide singleton holding the *current* viewport, written from every circuit. Currently latent (nothing subscribes), which is the only reason it has not surfaced.
- `MeshWeaver.Hosting.AspNetCore/Portal/PortalApplication.cs` — the off-circuit fallback address `portal/{userId}`. Correct for SSR/prerender by design, but it does mean every non-circuit scope of one account shares a hub.

## And the second rule: find the render site before you call it a defect

A Blazor component is reachable only if something renders it — a `<Tag />` in a `.razor`, a `DispatchView` over a control the view registry maps to it, or a routable `@page` in an assembly the router was given. **None of those is implied by the file existing, by it compiling, or by it being referenced in a doc-comment.** `ThreadSidePanelContent` had all three of the latter and none of the former, and the first version of this page reported its call sites as live.

So for any component-level claim, the check is mechanical and takes one command:

```bash
grep -rn "<TheComponent" --include='*.razor' src/          # a tag
grep -rn "WithView<.*, TheComponent>" --include='*.cs' src/ # the control→view seam
grep -rn "AddAdditionalAssemblies\|AdditionalAssemblies" src/ # which assemblies the router scans
```

An empty result for all three means the code you are reading does not run, and everything downstream of it is a hazard rather than a symptom. This is the same failure mode as a CI gate that never executes: the code is *present*, so it reads as *in force*.

**A dormant hazard is still worth removing** — that is what this change did — but say which it is. The difference decides whether you are fixing a symptom or closing a door.

## Related

- [User Interface](../UserInterface) · [Blazor Data Binding](../BlazorDataBinding) — bind the GUI directly to the node stream; this page is the boundary of that rule.
- [No Static State](../NoStaticState) — the same failure one layer down: process-wide state bleeding across users. A per-user node bleeds across *tabs*, and neither is fixed by a `Clear()`.
- [AccessContext Propagation](../AccessContextPropagation) — why the identity half of "who is this viewer" is already per-circuit.
- [Thread Operations](../ThreadOperations) — the composer → thread → round pipeline the context travels through.
