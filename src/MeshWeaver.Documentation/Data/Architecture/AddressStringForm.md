---
Name: The Address String Form
Category: Architecture
Description: "An Address has ONE string form, host~path, and it parses back to the same address. Why ToString() used to render path~host, what that reversed, and the one cross-process text that had to accept both forms during the roll."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h16"/><path d="M4 12h10"/><path d="M4 17h16"/><path d="M17 10l3 2-3 2"/></svg>
---

# The Address String Form

> **The rule:** `Address.ToString()`, `Address.ToFullString()`, the implicit `string` conversion and
> the JSON `AddressConverter` all write ONE form: `outermost_host~…~host~path`. For an unhosted
> address that is just its `Path`. The implicit `string → Address` parser reads that same form back,
> so `(Address)address.ToString()` equals `address`, including nested hosts.

## Why this needed fixing (#6604)

Until #6604, `ToString()` rendered `path~host`, but the parser splits on the FIRST `~` and treats
the left side as the host. Every key minted with `ToString()` and then parsed back therefore came back
REVERSED. `sync/g~ui/c` became `ui/c` hosted on `sync/g`. The keys that do this round trip are:

- the `IPodHubGrain` key. `PodHubGrain` turns its primary key into an `Address` (`Address address =
  AddressPath`) and looks up the local route with it.
- a `MessageHubGrain` key, resolved by `GetAddress(key)` on activation.

Neither raised an error. The pod-hub grain simply looked up the wrong local route, answered
`PodHubNotHere`, and the router fell back.

The parser, the wire serializer, `AccessContext.ObjectId` and `UserNodeType.IsPortalIdentity` all
used `host~path` already. `ToString()` was the outlier, and it now delegates to `ToFullString()`.

## Rules for callers

- **Want the node path? Use `Address.Path`.** The host chain says where an address is activated,
  not what it is. Never split `ToString()` to find a path or a type. `Address.Type` is `Segments[0]`.
- **A segment holds neither `/` nor `~`.** Both are structural in the string form. A node id that
  could contain `~` is escaped before it becomes a segment (`AppDirectory` encodes `~` as `~7E`).
  A raw `~` in a segment cannot round-trip, and the parser will read it as a host separator.
- **Exact equality round-trips only when no segment holds `/`.** Production does build such
  addresses: `KernelContainer` writes `new Address("kernelExec", hub.Address.Path)`, a whole path in
  ONE segment. Its string is the same text, and it parses back with the same `Path`, `Type` and host
  chain, but re-segmented (`kernelExec`, `activity`, `x`, `y` instead of `kernelExec`, `activity/x/y`),
  so `Address.Equals` is false. `StreamRoutingHotPathTest` pins that shape as production behaviour.
  So the guarantee is: `ToString()` and parsing agree on `Path` and on the host chain for every
  address, and on `Equals` for an address whose segments hold no `/`. Compare a key that may carry
  an embedded `/` by `Path` (or by its string), never by `Equals` against a parsed copy.

## The one text that crosses a roll

Grain keys and memory-stream ids are in-memory only, so nothing persisted used the old form. The one
cross-process TEXT that did is the shutdown banner, `Hub {subject} is shutting down`, inside a NACK
message. During a roll an old pod's NACK is read by a new pod. So:

- `ShutdownNack.Banner` writes the owner's `Path` as the subject, with no host chain.
- `ShutdownNack.IsAnsweredByOwner` reads a subject up to its first `~`. A banner from this release
  has no `~`. A banner from the previous release had `path~host`, and its path is the part before
  the first `~`.

## Where it is pinned

- `AddressStringRoundTripTest` (MeshWeaver.Messaging.Hub.Test): seeded property test over one to four
  segments and zero to three nested hosts. It checks the parse round trip and that the wire form
  agrees. A negative control shows the old rendering does not round-trip. `AnEmbeddedSlash_KeepsPathAndHost_ButNotSegmentation`
  pins the narrower guarantee for a segment that holds `/`.
- `OwnerAnswerRecognitionGuard.ABannerFromThePreviousRelease_IsStillRecognised`
  (MeshWeaver.Hosting.Test): the roll window.
