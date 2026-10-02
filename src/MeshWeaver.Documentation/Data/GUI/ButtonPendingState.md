---
Name: "Buttons: Pending State & Navigate-on-Accepted"
Category: Documentation
Description: Every framework button with a click action shows itself pressed from the click until the owner confirms it, refuses a second click meanwhile, restores itself with the reason on failure — and can navigate the moment the click is accepted
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="8" width="18" height="8" rx="4"/><path d="M12 3v2"/><path d="M12 19v2"/></svg>
---

A click on a framework button crosses the wire: the client posts a `ClickedEvent`, the owner's
per-stream `sync/{id}` hub runs the control's click action, and only then does anything visible
happen. Without feedback, that gap reads as "the button did nothing" — and invites a second click.
So every framework button with a click action has a **pending state**, generically, with no code
in the button's author.

# What the person sees

| Moment | Button |
|---|---|
| The click (synchronously, before any round trip) | disabled, a progress ring in place of its start icon, `aria-busy="true"`, tooltip **"Working…"** (`common.working`, en + de) |
| A second click while pending | ignored — the click is submitted exactly once |
| The owner **accepts** | restored; if the button declares `NavigateOnAccepted`, the page navigates there instantly |
| The owner **refuses** (the action failed, or the stream was gone) | restored, and the reason is shown through the portal's error sink — never a silent reset |

A button without a click action (for example one that only carries `WithNavigateToHref`) never
pends: it has nothing to wait for.

# What "done" means

The owner answers the click's receipt (`UserActionAccepted`) when the click action is **done**, and
answers a `DeliveryFailure` carrying the error when it fails. "Done" is the action's own completion
signal, normalised to ONE contract (`UiControl.ClickAction` is an `IObservable<Unit>` factory):

| The action is written as | Done when | Failed when |
|---|---|---|
| `WithClickAction(ctx => { …; return Task.CompletedTask; })` | immediately, on the owner's turn — exactly as before | it throws |
| `WithClickAction(ctx => { … })` (an `Action`) | immediately | it throws |
| `WithReactiveClickAction(ctx => someObservable)` (an `IObservable<Unit>`) | the observable **completes** (values are ignored) | it **errors**, or the action throws before returning it |

So a synchronous handler behaves as it always has, and a handler that wants the button to stay
pressed until its write is confirmed **returns that write** from `WithReactiveClickAction` — a
distinct name rather than a `WithClickAction` overload, because a lambda that fits both return types
(`_ => throw …`) would otherwise turn ambiguous in every existing caller, in-mesh sources included:

```csharp
Controls.Button(texts.Approve)
    .WithReactiveClickAction(ctx => ApproveAs(ctx.Host.Hub, access, caller, path)   // GetMeshNodeStream(path).Update(…)
        .Take(1)
        .Select(_ => Unit.Default))
    .WithNavigateOnAccepted(progressHref);
```

🚨 **Return the confirmation of the WRITE the click requests — never the long-running work that
write triggers.** An approval click writes `approvedBy`; the owning hub's watcher then runs the
deployment for minutes. The button must pend for the write (milliseconds), not the run: the run's
progress belongs on the page the button navigates to. An observable that never completes keeps the
button pending until the page goes away.

The framework subscribes the returned observable exactly once — do **not** also `.Subscribe()` it
inside the handler, or the write runs twice. Nothing here is `async`: no `await`, no `.ToTask()`, and
nothing parks the owner's turn; a still-running `Task` only registers a continuation, and the receipt
is a response (`ResponseFor`), so it carries the clicker's `AccessContext` whichever thread completes
the action.

## A one-off read in a click handler is RETURNED, never subscribed

The commonest click handler reads its form once and acts on it. Written as
`ctx.Host.Stream.GetDataStream<T>(formId).Take(1).Subscribe(data => …)` followed by
`return Task.CompletedTask`, it is broken twice over: the click is answered as done before the read
has emitted, and a fault in the read — or in the body that handles it — has **no observer**, so Rx
rethrows it on whatever thread produced it, the log names no area, and the person sees a button that
did nothing. Return the read instead:

```csharp
Controls.Button(host.Localize("ui.invite"))
    .WithReactiveClickAction(ctx =>
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Take(1)                                  // a one-off read for the click — not a live binding
            .Do(form => Submit(ctx, spacePath, form))
            .Select(_ => Unit.Default));
```

The host then owns the subscription: completion answers the click, and an error is logged with the
area and hub (`LayoutAreaHost.FailClick`) and refused to the client, whose button leaves its pending
state showing the reason. A helper the handler calls returns the observable too
(`ctx => RemoveCollectionItem(ctx.Host, …)`).

🚨 **The id the click reads must be SEEDED when the form is rendered** (`host.UpdateData(formId, new
Dictionary<string, object?> { … })` before the button exists). A data id that was never written emits
nothing and never completes (`GetDataStreamUnsetIdTest`), so `Take(1)` on it neither completes nor
faults: there is no bound on a returned click observable, and the button stays pending until the
page goes away. A field bound by pointer does not seed its form — the id is first written when the
person types. Seeding is also what lets an empty submit reach the handler's own validation message. Work that deliberately continues AFTER the click is
answered (an agent round, a background write) keeps its own `.Subscribe(onNext, onError)` whose error
arm reports and shows the fault — never a one-argument `Subscribe`.

`ClickActionSubscribeHasErrorArmGuard` (MeshWeaver.Documentation.Test) holds `src/` at zero
one-argument `Subscribe` calls inside a `WithClickAction` / `WithReactiveClickAction` lambda.

# Navigate-on-accepted

`ButtonControl.WithNavigateOnAccepted(href)` sets `NavigateOnAccepted`. On the owner's acceptance the
client navigates to `href` immediately — without waiting for anything the action started. Compare:

| Property | Navigates | Use for |
|---|---|---|
| `NavigateToHref` | at the click, before anything is sent | plain links styled as buttons |
| `NavigateOnAccepted` | on the owner's acceptance; a refused click stays on the page and says why | "do X, then show me X happening" — approve → the run's live progress |

# Client contract

The receipt travels through the existing acknowledged sender
(`ISynchronizationStream.SubmitUserAction`, see
[Refusing a Lost User Action](/Doc/Architecture/RefusingALostUserAction)); its five-argument overload
adds `onAccepted`, the other end of the pending state:

```csharp
stream.SubmitUserAction(new ClickedEvent(area, stream.StreamId), actingUser,
    onRefused: sentence => { /* restore + show the sentence */ },
    onAccepted: () => { /* restore, then navigate if NavigateOnAccepted is set */ });
```

Both callbacks run on whichever thread delivered the receipt; a view marshals back onto its own
dispatcher (`InvokeAsync`) before touching component state. The Blazor `ButtonView` (in
MeshWeaver.Plugins) is the reference implementation.

# See also

- [Layout Areas](../LayoutAreas) — click handlers and navigation between areas
- [Refusing a Lost User Action](/Doc/Architecture/RefusingALostUserAction) — the receipt this state waits on
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why handlers compose observables instead of awaiting
