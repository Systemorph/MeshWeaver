---
Name: Per-Tab Session State
Category: Architecture
Description: A mesh node is shared by every tab of one account by construction, so "which page is this viewer on" and "navigate ME there" must never live on a per-user node — and a hazard is only a defect once you have found the code that renders it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="9" height="16" rx="1"/><rect x="13" y="4" width="9" height="16" rx="1"/><path d="M6.5 9h0"/><path d="M17.5 9h0"/></svg>
---

# Per-Tab Session State

**A `MeshNode` is shared by every viewer who can read it — including the same person in a second browser tab. So any state whose meaning is "*this viewer, right now, in this window*" is wrong on a node, and no amount of care at the call sites can rescue it.** The two questions that always have this shape are:

- **"Which page is the viewer looking at?"** — the navigation context.
- **"Take *me* there."** — a navigation command addressed to one window.

This page came out of [MeshWeaver#3060](https://github.com/Systemorph/MeshWeaver/issues/3060) ("chat session cross-talk when the same account opens a second browser tab"). It carries two lessons: the scoping rule above, and a methodological one the investigation learned the hard way — **a mechanism you can read in the code is a hypothesis until you have found the code that RENDERS it.** The most incriminating call sites here turned out to sit in a component with no render site, and the first version of this page reported them as live.

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

## The concrete instance: the out-of-thread chat composer

The "new chat" composer is a **per-user singleton node**:

```csharp
// MeshWeaver.Plugins/src/MeshWeaver.AI/ThreadComposerNodeType.cs
public static string PathFor(string user) => $"{user}/{ThreadNodeType.ThreadPartition}/{NodeType}";
//                                            → {user}/_Thread/ThreadComposer
```

Its content (`ThreadComposer`) mixes three different lifetimes in one record:

| Field | What it really is | Correct scope |
|---|---|---|
| `Harness`, `AgentName`, `ModelName`, `Effort` | the user's last-used selection | **per user** — sharing across tabs is the feature |
| `MessageContent`, `Attachments` | the draft being typed | per tab (debatable: "my draft follows me" is a defensible per-user reading) |
| `ContextPath`, `ContextReference` | **which page this viewer is on** | **per tab** |
| `OpenThreadPath` | **"navigate ME to this thread"** | **per tab** |

### What is LIVE today

**The draft and the selections are one live binding across every tab.** The composer's default (`""`) layout area is reached at the composer NODE's own path through `ApplicationPage`'s catch-all — `ThreadNodeType`'s `BuildCreate` redirects "+ new chat" straight to `/{chatInputPath}` (`ThreadNodeType.cs:319`) — and out of a thread `ThreadChatView` also binds it (`_templatePath = ThreadComposerNodeType.PathFor(_userHome)`, `ThreadChatView.razor.cs:565`), projecting `Harness`/`AgentName`/`ModelName` from it (`:827`) and writing the user's default back (`WriteComposerSelection`, `:866`). So typing a draft, attaching a reference, or re-picking a model in one tab is observed live by the other. For the preference fields that is the intent; for the draft it is at best debatable.

### What is LATENT — and why the reachability check matters more than the code

`ContextPath` and `OpenThreadPath` are the two fields whose semantics are unambiguously per-tab, and **their writer and their consumer both live in a component that has no render site**:

- `MeshWeaver.Plugins/src/MeshWeaver.Blazor.Chat/ThreadSidePanelContent.razor.cs:77` → `:89` → `:105` writes `ContextPath` on every navigation, and `:166` → `:195`/`:199` consumes `OpenThreadPath` by navigating *its own* circuit.
- Nothing renders `ThreadSidePanelContent`. Its only mentions in either repo are its own two files and three doc-comments; `PortalLayoutBase.razor:282-316` renders `DispatchView` over a `ThreadChatControl` instead. The component was orphaned by the #977 module split (`3cc44431`).

So today: **nothing writes `ContextPath` on the per-user composer node**, and `ThreadComposerView.Send` (`:255`) reads `null`, falls to `ns = user`, and creates the thread in the user's own home. The cross-tab wrong-target write is a hazard the design *permits*, not a defect the product currently exhibits — and the one thing that would re-arm it is restoring a live writer for that field, which is exactly what "put the side panel back" would do.

One half of the pair is live and asymmetric, which is its own bug: `StartThreadIn`'s `onCreated` still stamps `OpenThreadPath = node.Path` (`ThreadComposerView.cs:338`), and **no live code consumes it** — so a Send from the composer area creates the thread, never navigates to it, and leaves a stale navigate-signal on the node until something clears it (`ThreadChatView.StartNewComposer`, `:3325`).

### Where the hazard would lead if a writer returned

`ContextPath` is not a label — it is the root the round's writes resolve against, so re-arming it is not a cosmetic regression:

```
ThreadComposerView.Send            (ThreadComposerView.cs:250)
  ns = ThreadNodeType.MainNodeOf(edited.ContextPath)          ← read off the SHARED node
  → StartThreadIn                  (ThreadComposerView.cs:312)
      hub.StartThread(namespacePath: ns, contextPath: contextPath, …)
        → the thread NODE is created under {ns}/_Thread/{speakingId}
        → request.ContextPath
          → ThreadExecution.cs:1770  client.SetContext(NavigationContextProjection.ToAgentContext(…))
            → AgentChatPaths.cs:20   MeshOperations.ResolveContextPath(chat.Context?.Context, path)
              → MeshPlugin.cs:87 patch · :103 edit_content · :130 recycle · :142 move · :156 copy
```

`OwningNamespace` (`ThreadComposerView.cs:288`) only redirects to the user's home when they *cannot* `Create` in the namespace, so inside any partition the user can write, the thread would land under the other tab's node — and a bare single-segment argument to an agent write tool (`patch("Notes", …)`) would resolve to `{otherTabsContext}/Notes`.

### What this does NOT explain

**The live chat surface takes its context per-circuit.** `ThreadChatView` derives `initialContext` from the circuit's own `INavigationService.NavigationContext` and submits with it (`ThreadChatView.razor.cs:1255-1322`); inside a thread the composer is the thread's own embedded `Thread.Composer`. So a *running* chat in tab A following tab B's page is **not** accounted for by anything on this page. The shared-node hazard is real and worth removing; it is not, on the evidence here, the reported symptom.

## The same shape elsewhere

`ThreadComposer` is the instance that produced #3060, but the pattern recurs wherever a singleton is keyed by user identity and holds a live session:

- `MeshWeaver.Plugins/src/MeshWeaver.AI/Connect/ConnectSessionManager.cs` — sessions keyed `{ownerPath}|{provider}`; `StartConnect` cancels the existing slot before overwriting it, so a provider sign-in started in tab B **kills tab A's live CLI session**.
- `MeshWeaver.Blazor.Portal/Resize/DimensionManager.cs` — a process-wide singleton holding the *current* viewport, written from every circuit. Currently latent (nothing subscribes), which is the only reason it has not surfaced.
- `MeshWeaver.Hosting.AspNetCore/Portal/PortalApplication.cs:84` — the off-circuit fallback address `portal/{userId}`. Correct for SSR/prerender by design, but it does mean every non-circuit scope of one account shares a hub.

## The rule

> **Ask what the value would mean to a second window of the same account. If the honest answer is "not that", it does not belong on a node keyed by user.**

Three shapes that are correct:

- **Per-viewer *preference*** (last-used model, panel position, theme) → a per-user node is right. Two tabs agreeing is the feature.
- **Per-viewer *session* state** (which page, which selection, an in-progress form) → the **layout area's own store**, which is already per-subscriber: `host.UpdateData(...)` / `/data/{id}` on the `LayoutAreaHost`. Nothing else in the stack needs changing, because the subscriber address already *is* the circuit.
- **A command addressed to one window** → carry the **addressee**. `LayoutAreaHost` can read its own subscriber (`Stream.Get<Address>(nameof(SubscribeRequest.Subscriber))`, `LayoutAreaHost.cs:1587`), which for a Blazor tab is `portal/{circuitId}`; a consumer must then act only on a signal addressed to it. A broadcast signal on shared state is not a command, it is an interrupt for everybody.

And one anti-pattern: **do not "fix" it by minting a node per tab** (`{user}/_Thread/ThreadComposer/{circuitId}`). Circuits are unbounded and short-lived; that trades a correctness bug for unbounded node churn and loses the durability the node was there for in the first place.

## And the second rule: find the render site before you call it a defect

A Blazor component is reachable only if something renders it — a `<Tag />` in a `.razor`, a `DispatchView` over a control the view registry maps to it, or a routable `@page` in an assembly the router was given. **None of those is implied by the file existing, by it compiling, or by it being referenced in a doc-comment.** `ThreadSidePanelContent` has all three of the latter and none of the former.

So for any component-level claim, the check is mechanical and takes one command:

```bash
grep -rn "<TheComponent" --include='*.razor' src/          # a tag
grep -rn "WithView<.*, TheComponent>" --include='*.cs' src/ # the control→view seam
grep -rn "AddAdditionalAssemblies\|AdditionalAssemblies" src/ # which assemblies the router scans
```

An empty result for all three means the code you are reading does not run, and everything downstream of it is a hazard rather than a symptom. This is the same failure mode as a CI gate that never executes: the code is *present*, so it reads as *in force*.

## Reproducing the seam

🚨 **What this reproduces is the SEAM, not a live user journey.** It drives the shared node directly, standing in for the writer that `ThreadSidePanelContent` would be if it were rendered. It proves that a write from one subscriber is observed by another and that `Send`'s namespace decision follows that shared field; it does **not** prove a live writer exists — see "What is LATENT" above. Run it before and after any change that gives `ContextPath` a live writer.

Two independent client hubs stand in for two circuits (each has its own workspace and its own subscription; a Blazor circuit's portal hub is exactly this shape). Against a `MonolithMeshTestBase` mesh with `AddAI()`:

1. Seed `{user}/_Thread/ThreadComposer`.
2. From hub **B**, `GetMeshNodeStream(composerPath).Update(… ContextPath = "TestData/PageB" …)` — the literal body of `WriteComposerContext`.
3. From hub **A**, `GetMeshNodeStream(composerPath).Select(ComposerOf)` observes `ContextPath == "TestData/PageB"`. Hub A never navigated.
4. Run `Send`'s decision on hub A — `ThreadNodeType.MainNodeOf(ContextPath)` → `hub.StartThread(namespacePath: …)` — and the created thread's path starts with `TestData/PageB/`.
5. Stamp `OpenThreadPath` from hub B; hub A's `Select(…OpenThreadPath).Where(non-empty).DistinctUntilChanged()` — `ThreadSidePanelContent`'s observer verbatim — emits it. That value is the argument to `NavigateTo`.

Steps 2–3 are the bleed, step 4 is the wrong-target write the bleed enables, step 5 is the navigation hijack. All three passed on 2026-09-03 against MeshWeaver.Plugins `6a3bceff`.

**And the negative control**, because an assertion that cannot fail is not an assertion. Re-run with the three expectations flipped to the *isolated* outcome and all three fail, each printing the bleed verbatim:

```text
ContextPath_WrittenByTabB_IsObservedByTabA
  Expected the observable to emit a value matching the predicate within 46s, but it did not.
  Last of 1 emission(s) was: ThreadComposer { … ContextPath = TestData/PageB … }
      ← hub A's ONLY emission carries hub B's context

SendFromTabA_CreatesTheThreadUnderTabBsContext
  Expected "TestData/PageB/_Thread/hello-from-tab-a-89dd" to start with "TestData/PageA/"
      ← the thread hub A started was created under hub B's node

OpenThreadPath_StampedByTabB_ReachesTabAsNavigationObserver
  Last of 1 emission(s) was: TestData/PageB/_Thread/c2c708d0e008416b9707bc7176972f03
      ← the argument hub A's observer hands to NavigateTo
```

The repro is deliberately **not** committed as a test: it asserts the defect, so it would turn red the moment the defect is fixed. The recipe above is the durable form.

## Related

- [User Interface](../UserInterface) · [Blazor Data Binding](../BlazorDataBinding) — bind the GUI directly to the node stream; this page is the boundary of that rule.
- [No Static State](../NoStaticState) — the same failure one layer down: process-wide state bleeding across users. A per-user node bleeds across *tabs*, and neither is fixed by a `Clear()`.
- [AccessContext Propagation](../AccessContextPropagation) — why the identity half of "who is this viewer" is already per-circuit.
- [Thread Operations](../ThreadOperations) — the composer → thread → round pipeline the context travels through.
