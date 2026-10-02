---
Name: Input From the Subscriber
Category: Architecture
Description: A layout area is rendered once per subscriber, and its stream accepts that subscriber's input — a click, a blur, a dialog dismissal, an edited value — only from the identity it was subscribed under. Anything else is refused with an answer and a Warning before a handler runs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="8" cy="8" r="3"/><path d="M2.5 20a5.5 5.5 0 0 1 11 0"/><path d="M15 9h6M18 6l3 3-3 3"/></svg>
---

# Input From the Subscriber

A layout area is rendered **once per subscription**: every `SubscribeRequest` for a
`LayoutAreaReference` gets its own `LayoutAreaHost`, its own control tree and its own click-action
closures, rendered under the identity that subscribed. The input that stream receives belongs to
the same identity.

**The rule: a layout stream accepts its subscriber's input only from the identity the stream was
subscribed under.** Input is

| Message | What it is |
|---|---|
| `ClickedEvent`, `BlurEvent`, `CloseDialogEvent` — every `IUserAction` | a person's action on a control |
| `PatchDataChangeRequest` | a value the subscriber edited (a data-bound field) |

A delivery of one of these whose `AccessContext.ObjectId` is not the subscriber's is **refused**:

- no handler runs — not `LayoutAreaHost.OnClick` / `OnBlur` / `OnCloseDialog`, not the data update;
- the sender is answered with a `DeliveryFailure` carrying `ErrorType.Forbidden` and a localized
  sentence (`error.userActionNotFromSubscriber`, `error.inputNotFromSubscriber`), which a view shows
  through `SubmitUserAction`'s `onRefused`;
- the owner logs one Warning naming the stream, the area, the subscriber and the identity the
  delivery carried.

## It fails closed

An input is accepted because both identities are **known and equal**. A delivery with no identity
is refused; a stream with no recorded subscriber identity refuses every input. There is no
exemption by sender: the platform identity is held to the same rule as a person, and there is no
per-action or per-control opt-out.

## Where the check sits

One place: a delivery-pipeline step on the stream's own synchronization hub
(`SynchronizationStream.AcceptInputFromSubscriberOnly`). It runs in front of the hub's rule chain,
so it decides before any handler sees the delivery, and it holds however the delivery was
addressed — at the owner (routed to the stream by its id) or at the stream's hub directly.

The identity it compares against is recorded when the stream is created:

- `StreamConfiguration.SubscriberIdentity` — the `AccessContext` of the `SubscribeRequest` delivery,
  set by the owner when it builds the stream for that subscriber;
- for a stream opened without a subscribe, the viewer the `LayoutAreaHost` captured at
  construction (`LayoutAreaHost.ViewerContext`).

A stream opts in with `StreamConfiguration.WithInputFromSubscriberOnly`; `LayoutAreaHost` does, for
every layout area. **Data streams do not**: a node or collection stream is shared by design — one
mirror serves many writers — and each write through it is authorized under the writer's own
identity.

## What a sender has to do

Nothing new, if it already sends as the subscriber:

- a Blazor view submits through `stream.SubmitUserAction(action, actingUser, …)` with the circuit's
  user, the identity its portal hub subscribed under;
- a participant connection (gRPC, SignalR) has every delivery — the subscribe and the action alike —
  stamped with the connection's validated identity;
- a test posts under the identity it subscribed with.

A session whose identity **changes** after a stream was subscribed (a sign-in, an onboarding that
resolves the user) is a different identity to that stream: the view must subscribe again, and until
it does its actions are refused with the sentence above.

## Related

- [Refusing a Lost User Action](../RefusingALostUserAction) — the other refusal an action can meet:
  its stream is already gone.
- [Participant Ingress](../ParticipantIngress) — what a client connection may post, and how its
  identity is stamped.
- [AccessContext Propagation](../AccessContextPropagation) — how an identity reaches a delivery.
- [Data Binding](/Doc/GUI/DataBinding) — the edited values this rule covers.
