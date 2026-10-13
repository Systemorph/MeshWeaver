---
Name: The Sender Is the Posting Hub
Category: Architecture
Description: A post's sender is stamped by the hub that actually posts it; a sender the caller wrote is overwritten, so a guard that decides by sender cannot be passed by naming another hub's address.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22l-4-9-9-4 20-7z"/></svg>
---

# The Sender Is the Posting Hub

Some hub guards decide by the **sender** of a delivery. The clearest one is the
[stamped-field guard](../StampedFields): a node's hub admits a write to a stamped field only from
itself, and everything arriving from another address is a client write. Such a guard is only as good
as the sender it reads.

`PostOptions` is a record, so posting code can write `o => o.WithTarget(x) with { Sender = y }`.
Until #6553 the delivery left with whatever address the caller wrote: code running in the portal
process — a compiled module, a NodeType or Code node the mesh compiled — could name a node's own
address as the sender of a patch and pass that node's owner-only guard. No external client could do
it (the wire ingresses stamp every delivery they accept, see [Participant Ingress](../ParticipantIngress)),
but in-process code could.

## The rule

**`hub.Post` stamps the sender itself: it is the address of the hub that posts.** A sender the
caller wrote that differs is replaced by the posting hub's address, and the hub logs one
`[ForeignSender]` warning per message type and call site, naming the claimed address and the
calling code's stack. The key holds only stable data — the claimed address is caller-controlled
and may be fresh on every post — and the set is capped per hub, so a misbehaving caller cannot grow
it or flood the log. The call site is read off the stack, skipping the hub's own `Post`/`Observe`
plumbing and Rx, so it names the code that wrote the sender.
The post is not refused — it goes out as the hub that actually sent it, so the reply comes back to
that hub and a sender-keyed guard judges the real poster.

There is one sanctioned exception, and only the messaging assembly can mark it
(`PostOptions.CarriedByParent()`, internal): **a parent carrying a post its hosted hub can no longer
send itself** — a correlated reply or a release (`IReleasesRemoteState`) posted while that hub is
tearing down. The carried delivery keeps the hosted hub's address (host-qualified on the way up, the
same qualification ordinary upward routing applies), because that is who posted it.

Everything else that legitimately moves a delivery under an address other than the current hub's is
**routing**, not posting: hierarchical routing re-qualifies the sender with its host on the way up,
the routing services and grains forward a delivery whose sender was stamped where it was posted, and
`ResponseFor` retargets a reply at the request's sender without touching the reply's own sender.
None of these go through a caller-written `Sender`.

## Measured

- `test/MeshWeaver.Messaging.Hub.Test/SenderIsThePostingHubTest.cs` — a client posting with the
  target's own address, or an unrelated one, as sender reaches the target stamped with its real
  address and gets the reply; the control is an ordinary post. With the stamping disabled both forged
  arms time out (the reply went to the forged address).
- `test/MeshWeaver.Graph.Test/StampedFieldsTest.cs` →
  `ClientPatch_ForgingTheOwningHubAsSender_IsStillRefused` — a client patch of a stamped field that
  names the owning node's address as its sender is refused naming the field and the stored stamp
  survives. With the stamping disabled it fails: the refusal never reaches the poster.
- The sanctioned carry: `ReleaseLeavesATearingDownHubTest` (a release from a tearing-down hosted hub
  arrives under the same sender as its live post) and the teardown reply tests still pass.

## Not established

- **A hand-built envelope.** `MessageDelivery<T>` has public constructors that take a sender, and
  `IMessageHub.DeliverMessage` / `IRoutingService.DeliverMessage` accept any delivery — the routing
  layer, the Orleans grains and the SignalR and gRPC ingresses need exactly that. In-process code
  that builds a delivery itself and hands it to one of those does not go through `hub.Post`, so its
  sender is not stamped. Closing that needs a caller check on those entry points (the
  [in-mesh impersonation guard](../InMeshImpersonation)'s calling-assembly classification measured
  about 0.3 µs per call, which is a per-hop cost on the routing path) or envelopes only the platform
  can construct; neither is in place.
- **Holding another hub's object.** Code that obtains another hub's `IMessageHub` instance and posts
  through it posts AS that hub. The stamping answers "who posted", and that hub did.
- Satellite repositories and mesh content were searched for code that writes a foreign sender into a
  post: none was found (a grep of every satellite checkout for `Sender =` on post options). The only
  sites that build deliveries with an explicit sender are the wire ingresses and tests.
