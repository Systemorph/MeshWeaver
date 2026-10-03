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

**The rule: every input that changes a layout stream is accepted only from its subscriber.**
Every message type the stream's synchronization hub handles has a role in one explicit, closed
classification (`StreamInputRule`), and the role says who may send it:

| Message | Role | Accepted from |
|---|---|---|
| `ClickedEvent`, `BlurEvent`, `CloseDialogEvent` — every `IUserAction` | input: a person's action on a control | the subscriber's identity |
| `PatchDataChangeRequest` | input: a value the subscriber edited (a data-bound field) | the subscriber's identity |
| `DataChangeRequest` | input: a data write handed to the workspace | the subscriber's identity |
| `DataChangedEvent` | input: a frame applied as the stream's state — frames flow from an owner to its mirrors, and a layout stream has no upstream | the subscriber's identity |
| `StreamErrorEvent` | input: ends the stream in error — like a frame, it flows from an owner to its mirrors | the subscriber's identity |
| `UpdateStreamRequest`, `SetCurrentRequest` | the stream's own write path (`Update`, `OnNext`) | the stream itself only — every own write carries a per-stream token no other party holds |
| `UnsubscribeRequest` | the end of the subscription | the subscriber's identity, or the mesh's own hubs |
| `GetDataResponse`, `DeliveryFailure` | answers to deliveries the stream's hub sent | the subscriber's identity, or the mesh's own hubs |

Why the last two rows are not held to the subscriber's identity alone: a subscribing hub releases
its subscription while it is tearing down, as a system message that need not carry the
subscriber's identity, and that release is the only thing that frees the owner's per-subscriber
stream; an answer is issued by whichever hub answers. Both are still refused when they arrive
through a participant connection (`ParticipantIngress`) under any identity but the subscriber's.

A delivery that does not meet its row is **refused**:

- no handler runs — not `LayoutAreaHost.OnClick` / `OnBlur` / `OnCloseDialog`, not the data
  update, not the write;
- the sender is answered with a `DeliveryFailure` carrying `ErrorType.Forbidden` and a localized
  sentence (`error.userActionNotFromSubscriber`, `error.inputNotFromSubscriber`), which a view shows
  through `SubmitUserAction`'s `onRefused` (an answer that is refused is not answered again);
- the owner logs one Warning naming the stream, the message's role, the area, the subscriber and
  the identity the delivery carried.

**The classification is closed.** The stream registers each of its handlers through one helper
that records the type, and a stream whose hub handles a type the classification does not name is
not built — the construction fails, naming the type. A new handler therefore reaches no stream
until it has been given a role. `StreamInputRuleClassifiesEveryHandledTypeTest` pins the set.

## It fails closed

An input is accepted because both identities are **known and equal**. A delivery with no identity
is refused; a stream with no recorded subscriber identity refuses every input. There is no
exemption by sender for input: the platform identity is held to the same rule as a person, and
there is no per-action or per-control opt-out. A message type the classification does not name
cannot be handled by the stream at all.

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
