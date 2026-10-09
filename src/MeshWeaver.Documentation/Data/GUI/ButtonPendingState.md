---
Name: "Buttons: Pending State & Navigate-on-Accepted"
Category: Documentation
Description: Every framework button with a click action is busy from the click until its work settles — disabled, a status line saying what is happening, progress, Cancel, the error or the outcome — never runs twice for a double click, and can navigate the moment the click is accepted
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
| While the click runs | still disabled; beside it the **status line** the action reports (or "Working…"), a progress bar when the action knows its fraction, and a **Cancel** button (`click.cancel`) |
| A second click while running — this tab, another tab of the same page, a retried delivery | ignored by the client, and **joined** by the owner: the action never runs twice (see *Idempotence*) |
| The click **settles** | re-enabled; the **outcome line** the action stated (`ctx.ReportSummary`), if any, stays beside it; with `NavigateOnAccepted` the page navigates as soon as the action is accepted |
| The click **fails** (the action errored, or a tracked activity failed) | re-enabled, and the **reason** is shown beside it and through the portal's error sink — never a silent reset |
| **Cancel** pressed | "Cancelling…", then re-enabled with "Cancelled" once the work stopped |

A button without a click action (for example one that only carries `WithNavigateToHref`) never
pends: it has nothing to wait for. None of this needs code in the button's author — the next
sections are for actions that have more to say.

# The busy state is written by the owner

The client's pressed state lasts until the receipt; the BUSY state lasts until the work settles,
and the owner states it. When a click starts, the owner's `LayoutAreaHost` opens a click session and
writes a `ClickProgress` into the stream's data section under `ClickProgress.DataId(area)`; a client
binds it by `ClickProgress.PointerFor(area)` (row-scoped controls: `ClickProgress.RowArea(area, row)`).

| `ClickProgress` field | Meaning |
|---|---|
| `Running` | true from the click until the action and everything it tracks settled — the control stays disabled |
| `Status` | the status line, "what is happening"; null → the client's localized "Working…" |
| `Fraction` | completed fraction in [0, 1]; null → indeterminate |
| `Cancellable`, `Cancelling` | whether Cancel is offered / being honoured |
| `Error` | why the click failed — shown, never swallowed |
| `Summary` | the outcome line once settled |
| `ActivityPath` | the activity the click is bound to, when it tracks one |

The action talks to it through its context — all safe from any thread, all no-ops outside a click:

```csharp
Controls.Button(texts.ApproveSelected)
    .WithReactiveClickAction(ctx => ctx.SelectedRowKeys(selectionId)
        .SelectMany(keys => keys
            .Select((key, i) => Observable.Defer(() =>
            {
                ctx.ReportProgress(texts.Approving(i + 1, keys.Count), (i + 1.0) / keys.Count);
                return Approve(ctx.Host, key);                 // the write's confirmation
            }))
            .Concat()                                          // one at a time; Cancel disposes the rest
            .Count()
            .Do(n => ctx.ReportSummary(texts.Approved(n, keys.Count))))
        .Select(_ => Unit.Default));
```

| Call | Does |
|---|---|
| `ctx.ReportProgress(status, fraction?)` | updates the status line and fraction |
| `ctx.ReportSummary(text)` | sets the outcome line shown after the click settled |
| `ctx.CancellationToken` | trips on Cancel — and only on Cancel: settling, or the viewer leaving the page, does not stop work the click started |
| `ctx.OnCancel(handler)` | what Cancel must do beyond disposing the action's pipeline (runs on the owner, as the clicker) |
| `ctx.Track(IObservable<ClickProgress>)` | keeps the control busy over work the click STARTED that outlives the action; an emission with `Running = false` (or completion) ends it, one with `Error` fails the click |
| `ctx.TrackActivity(path)` (Mesh.Contract) | binds the busy state to an `ActivityLog`: its latest message is the status line, its terminal status settles the click (Failed → the error), and Cancel patches its `RequestedStatus` through `hub.CancelActivity` and waits for it to land |
| `ctx.TrackNode(path, node => ClickProgress?)` (Mesh.Contract) | stays busy until a node says the click's effect landed — e.g. an approval click that writes `RequestedAction` stays busy until the owner's watcher has moved the request on |

Strings passed in are shown as given — pass them localized (`ctx.Host.Localize(…)`). The activity
and node trackers read through `GetMeshNodeStream(path)`, so the node must exist when they are
called ([CQRS](/Doc/Architecture/CqrsAndContentAccess)).

## Cancel is a property write

A client's Cancel writes `requestedSession: <ClickProgress.Session>` at
`ClickProgress.CancelPointerFor(area)` — the session number of the click it is SHOWING, carried by the
request itself, so a Cancel delayed past the next click names the old session and is ignored. The owner watches it, exactly as an activity's control plane watches
`RequestedStatus` ([Activity Control Plane](/Doc/Architecture/ActivityControlPlane)): no verb message.
On Cancel the owner disposes the action's pipeline (a reactive action is therefore always
cancellable), trips the token, runs the `OnCancel` handlers as the clicker, refuses a still-open
receipt with "Cancelled", drops generic tracked sources and keeps watching an activity until it
reports Cancelled.

## Idempotence

While a click runs, a second `ClickedEvent` for the same **area, row and payload** joins it: the
action is not invoked again, and the duplicate's receipt gets the first click's outcome. Two pins on
one map (same area, different payload) or two rows of one template are different clicks — and
each row needs its own state: a row-scoped click is keyed by the row's `Key` (a grid sets it from
`DataGridControl.RowKey` / `WithRowKey`), path, pointer or index. A row known only by its value has
none, so while a click runs on such a control a click on another of its rows is **refused**
("Another action on this control is still running", `click.busyElsewhere`) rather than sharing — and
overwriting — the running click's state. The window
is the click's own lifetime — once it settled, the next click is a new one; an action that must be
idempotent across clicks (an approval that may only happen once) still says so in its own state, as
the domain owner's watcher does. The session is per layout-area stream, so two viewers each get
their own.

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
area and hub (the click session's failure arm) and refused to the client, whose button leaves its pending
state showing the reason. A helper the handler calls returns the observable too
(`ctx => RemoveCollectionItem(ctx.Host, …)`).

🚨 **The id the click reads must be SEEDED when the form is rendered** (`host.UpdateData(formId, new
Dictionary<string, object?> { … })` before the button exists). A data id that was never written emits
nothing and never completes (`GetDataStreamUnsetIdTest`), so `Take(1)` on it neither completes nor
faults: there is no bound on a returned click observable, and the button stays pending until the
page goes away. A field bound by pointer does not seed its form — the id is first written when the
person types. Seeding is also what lets an empty submit reach the handler's own validation message.
`ClickReadIdIsSeededGuard` (MeshWeaver.Documentation.Test) enforces the rule lexically. Every
`GetDataStream<T>(id).Take(1)` inside a click lambda in `src/` needs an `UpdateData(id, …)` in the
same file **outside** every click lambda. A write that only another click performs (the
"open a file, then Save" shape) does not count, because nothing guarantees which click comes first.
The guard does not prove that the seed runs before the button renders. It is not a timeout either:
a bound on the click would turn a missing seed into a late error instead of removing it. Work that deliberately continues AFTER the click is
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
dispatcher (`InvokeAsync`) before touching component state. A view also binds
`ClickProgress.PointerFor(area)` and stays disabled while it reads `Running = true`, renders
`Status`/`Fraction`/`Cancellable` (Cancel writes `ClickProgress.CancelPointerFor(area)`), and shows
`Error` or `Summary` once settled. The Blazor `ButtonView` (in MeshWeaver.Plugins) is the reference
implementation.

# See also

- [Layout Areas](../LayoutAreas) — click handlers and navigation between areas
- [Refusing a Lost User Action](/Doc/Architecture/RefusingALostUserAction) — the receipt this state waits on
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why handlers compose observables instead of awaiting
