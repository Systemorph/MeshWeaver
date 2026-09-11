---
Name: Refusing a Lost User Action
Category: Architecture
Description: A user action is refused visibly when its stream is already gone, and an accepted action now holds the sender's ordinary quiesce drain until its owner-side handler acknowledges it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11.5 4.5 7 6 5.5 9 8.5 15 2.5l1.5 1.5z"/><path d="M3 13a9 9 0 1 0 9-9"/><line x1="12" y1="12" x2="12" y2="12.01"/></svg>
---

# Refusing a Lost User Action

**A person clicked a button. The framework accepted the click, threw it away, and wrote one Warning
five seconds later that reads exactly like routine stream churn. Nothing retried, nothing surfaced,
and the next thing that looked for the result saw an absence indistinguishable from "you never
clicked."**

That is issue #3566, and it is the reason `IUserAction` exists.

## What was measured

MeshWeaver.Education run
[34042620439](https://github.com/Systemorph/MeshWeaver.Education/actions/runs/34042620439), job
`101512796719` — the Store **Install** button.

```
15:44:38.03  Click … click action done                                (no error)
15:44:38.06  Navigate to "/Store"                                     ← 20 ms later
15:44:38.205 Circuit connection DOWN … disposing per-circuit portal hub
15:44:38.41  ClickedEvent arrives at e2e-admin/Packages — sync/{id} already gone
15:44:43.41  warn: Dropping ClickedEvent for stream JRGdthy… : no synchronization hub
             found on this hub or any parent — the target stream is gone
```

`InstallPackage` was never invoked. Attempt 1 installed nothing, and nothing was red.

Two facts make this more than "the circuit was gone, so of course":

- **The action does not need the circuit, and the framework already knows that.**
  `InstallPackage` ends in `RunAsSystem(...).Subscribe(...)` — fire-and-forget. In the very same run
  the retry's install ran to completion **850 ms after its own circuit closed**.
- **It is not decided by how fast you navigate.** The retry had a *tighter* window (4 ms
  click→navigate, circuit closed 90 ms after the click) and it won. What differed was load: the
  failing attempt's card locator took 2 939 ms to settle versus 905 ms. The outcome turns on server
  load at the moment of the click — rare in testing, reachable in production.

The denominator: `Dropping ClickedEvent` appears **exactly once** in that run — that click — and
**zero** times in run 34080328179, where the same install succeeded.

## The scope call, and why it went the way it did

The issue names two coherent answers and picks neither. Both were examined; the first turns out not
to be implementable as written.

### "Deliver it anyway" — measured, and false as stated

> *routing the event to the target hub without requiring a live sync hub would make the click land*

It would not. Three things have to be true for a `ClickedEvent` to run, and the disposal takes all
three away at once:

1. The **handler** is `LayoutAreaHost.OnClick`, registered on the per-stream `sync/{id}` sub-hub —
   it is the only `ClickedEvent` registration in the framework. The owner hub itself
   (`e2e-admin/Packages`) has none, so an event routed there would be `Ignored`, not run.
2. The **action** is `control.ClickAction`, a closure held in the `EntityStore` snapshot of the
   `LayoutAreaHost` that was just disposed with the stream.
3. The handler's own filter is `Stream.ClientId.Equals(delivery.Message.StreamId)` — it is scoped to
   the departed subscriber by construction.

Making the click land therefore means **re-materialising a layout area for a subscriber that no
longer exists**: re-running the view function against state that has since moved, under an
`AccessContext` nobody is holding. That is a different design with its own answer to "whose identity
runs this", not a routing tweak. It is deliberately **not** what this change does.

### "Refuse it visibly" — what shipped

The drop stays a drop. What ends is its silence, on both of the audiences that exist:

| audience | before | after |
|---|---|---|
| the person, when anything of theirs is still attached | nothing | a `DeliveryFailure` to the sender, which a live portal hub raises as the standard error modal (`PortalErrorReporting` → `PortalErrorSink`) |
| the operator | one `Warning` worded as stream churn | one `Error` naming the action, the area and the stream, and saying the action did not run and never will |

🚨 **In the #3566 timeline itself the circuit was already gone, so the modal reaches nobody — and
that is honest, not a gap.** The person had navigated away; there is no live surface to write to,
and inventing one (a cross-session notification for a lost click) would be a feature, not a fix. The
other two ways into the same code path — a **released read stream** and a **reaped sync hub** on a
client that is still there — *do* have a live sender, and those are exactly the cases where somebody
is looking at a page that silently did nothing.

## The one line that separates the two classes

```csharp
public interface IUserAction : IRequest<UserActionAccepted>
{
    string ActionArea { get; }
}
```

`ClickedEvent`, `BlurEvent` and `CloseDialogEvent` implement it. Nothing else does, and nothing about
routing or handling changes — it exists **only** so that `DataExtensions.RefuseStreamMessage` can ask
one question:

- **A data frame** (`DataChangedEvent` & co) whose stream is gone is **dropped**, exactly as before.
  The only party that wanted it is the subscriber that has gone away, and its view went with it.
  NACKing every one would be pure teardown noise — the reason the framework
  [deliberately never did](/Doc/Architecture/ErrorPropagationAndWedges).
- **A user action** whose stream is gone is **refused**.

That asymmetry is the whole design. It is also what makes the change measurable: the regression test
asserts the refusal *and* asserts that a data frame on an identically-gone stream still produces
nothing (`DroppedUserActionIsRefusedTest`).

### Why `ErrorType.Rejected`

Not `NotFound` — `PortalErrorReporting` swallows a routing `NotFound` as benign churn, and would
swallow this with it. Not `ShuttingDown` — that is the transient "the address may come back, ride it
out" verdict, and this address is not coming back for this stream. `Rejected` is what it is: the
framework explicitly declined to run the action.

### Why the log line is `Error`

Log levels are a production cost model, not a debug dial, so a level is only ever raised with the
trade stated. Here it is: a lost user action is **work the platform accepted and threw away**, and it
is rare by construction — once in run 34042620439, zero times in the green run. Neither of the two
volume complaints applies: `StreamEndedEvent` (the "we get tons of this" case, maintainer
2026-09-01) stays at `Debug`, and data-sync frames stay at `Warning`. Only the user-action class
rises.

### The sentence a person reads

`error.userActionNotRun`, resolved from the catalog against the **acting user's**
`AccessContext.Locale` — the one carried by the delivery being refused — never an ambient culture,
which on Blazor Server is the container's and identical for every simultaneous viewer. See
[Localization](/Doc/Architecture/Localization).

## The ordering fix that followed

The visible refusal closed the silent-failure half, but it did not stop an accepted action losing a
race with circuit teardown. That second half is issue #3986 and is now an acknowledgement protocol:

1. `IUserAction` is an `IRequest<UserActionAccepted>`.
2. The Blazor sync hub uses `Observe` to register the response callback **before** it posts the
   click, blur, or dialog dismissal.
3. The owner-side `LayoutAreaHost` posts `UserActionAccepted` only after its stream-scoped handler
   has accepted the action.
4. A circuit close reaches the sync hub's existing **Quiescing** phase and sees that callback as
   pending. It therefore keeps the stream subscription alive until the receipt lands, then disposes
   normally.

There is no retry, grace extension, timer, or second disposal gate. The receipt makes the accepted
action part of the lifecycle mechanism the hub already drains. An action whose stream was genuinely
gone before it arrived is still refused by the path documented above.

### 🚨 Step 4 was only half true, and the half that was missing is the one that loses the click

A pending callback is drained in **Quiescing**, and Quiescing is a phase of the HUB. So the
acknowledgement orders ahead of the release only if the release is posted from a point that comes
*after* Quiescing. It was not.

The release is one line — the `UnsubscribeRequest` that destroys the owner-side `sync/{id}` sub-hub,
registered in `JsonSynchronizationStream.CreateExternalClient`. It was registered on the **stream**:

```csharp
reduced.RegisterForDisposal(new AnonymousDisposable(
    () => hub.Post(new UnsubscribeRequest(reduced.StreamId), o => o.WithTarget(owner))));
```

and `SynchronizationStream.Dispose()` disposes its registrants **synchronously, and deliberately
before `Hub.Dispose()`** — that ordering is [#1613's own fix](../StreamLivenessAndTheHubReference)
and is correct for what it was for (it is what removes the pending `SubscribeRequest` callback
promptly). Its cost here is that **the whole disposal ordering runs before the hub has a phase in
which to wait**. So the two teardown routes behaved differently:

| route | what disposes first | did the receipt order ahead? |
|---|---|---|
| the per-circuit portal hub disposes its hosted `sync/{id}` | the HUB — `streamDisposables` run from its `DisposeImpl` in ShutDown | yes, Quiescing came first |
| the STREAM is disposed directly — a workspace eviction, `ReclaimIfUnheld`, `EvictClientSubscriptions`, a consumer's `.Finally(stream.Dispose)` | the STREAM — synchronously, ahead of `Hub.Dispose()` | **no** |

The second route is not an edge: *released read stream* is one of the three ways into
`RefuseStreamMessage` this page already names, and it is the one where the person is still sitting
in front of the page.

**The fix is where the line is registered, not what it does.** It now goes on the stream's hub, so it
runs from `DisposeImpl` in ShutDown — strictly after Quiescing — on **both** routes:

```csharp
var release = new AnonymousDisposable(
    () => hub.Post(new UnsubscribeRequest(reduced.StreamId), o => o.WithTarget(owner)));
if (reducedHub is not null) reducedHub.RegisterForDisposal(release);
else                        reduced.RegisterForDisposal(release);   // no hub left to wait in
```

Nothing new waits, nothing is delayed "to be safe": the release simply sits behind the drain the hub
already performs.

### The sender is a surface, not a call shape — `stream.SubmitUserAction(...)`

The ordering above is only armed if the sender registered the callback, which `Post` does not do. So
the acknowledged send is a named surface — `UserActionSubmission.SubmitUserAction`, an
`ISynchronizationStream` extension — rather than an `Observe` incantation copied into every view
that raises a click. It carries the acting user's `AccessContext` (a user action must; the sync hub
has no identity of its own), owns its own subscription, and hands a refusal to the caller as the
already-localized `error.userActionNotRun` sentence.

That last part is also a behaviour change worth stating: with no callback registered, a refusal's
`DeliveryFailure` fell through to the mirror's blanket `DeliveryFailure` handler, which answers
`OnError` — **faulting the whole synchronization stream**, so every view bound to it died over one
lost click. Matched to the action it belongs to, it stops being a page-level fault and becomes a
sentence about that action.

### The measurement

`UserActionOutlivesStreamReleaseTest` asserts both directions against real hubs and a real remote
stream, with the owner-side `sync/{id}` sub-hub's own `DisposalCompleted` as the instrument:

| test | asserts | goes red on |
|---|---|---|
| `AnAcceptedActionHoldsTheReleaseUntilTheOwnerAnswers` | the owner's sub-hub does not die while an action is owed, and does die once it is answered | the defect |
| `AnOrdinaryReleaseIsPrompt` | a release with nothing owed still reaches the owner | "never release the stream", which would satisfy the first test alone |
| `AnActionOnALiveStreamStillRuns` | the acknowledged path still INVOKES the action | an ordering guarantee that stopped delivering clicks |

The owed-work window is made deterministic rather than raced: the action names a stream id with no
`sync/{id}` on the owner, so the owner holds it for `SyncStreamOptions.SyncHubRegistrationGrace`
(400 ms in the test, well inside the hub's 2 s Quiescing budget) and then refuses — the real
reaped-sync-hub shape. **Falsified by re-registering the release on the stream and rerunning:
`AnAcceptedActionHoldsTheReleaseUntilTheOwnerAnswers` fails at 200 ms** — *"Expected the observable
not to emit … but it emitted ()"* — while the other two stay green.

### What is still owed, and where

The **Blazor sender** (`BlazorView.OnClick` / `OnBlur` / the dialog close handlers, plus
`GoogleMapView` and `AppleMapView`) still calls `Stream.Hub.Post(new ClickedEvent(...))`. Until those
call sites move to `SubmitUserAction`, the portal registers no callback and the ordering above is
armed but unused. That half lives in MeshWeaver.Plugins and needs a platform pin carrying this
commit.

## What this deliberately does not do

- **Any retry, resubscribe or widened grace.** The issue rules all three out and so does this: an
  event that is genuinely undeliverable is not made deliverable by polling for a stream that is gone,
  and moving the 5-second grace only moves the cliff.
- **The harness half.** A Store install click with no outcome check and no wait — unlike its sibling
  `install()` helper, which retries in rounds — stays in MeshWeaver.Education#275.

## Related

- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — why fire-and-forget traffic is not
  NACKed during teardown, which is the rule this change makes one exception to and states.
- [Stream Liveness and the Hub Reference](../StreamLivenessAndTheHubReference) — how a stream and its
  sub-hub come apart, which is the state this page starts from.
- [Hub Disposal Model](../HubDisposalModel) — the Quiescing callback drain that now retains accepted
  user actions until their owner-side receipt lands.
- [Localization](../Localization) — the catalog and the explicit-locale rule.
