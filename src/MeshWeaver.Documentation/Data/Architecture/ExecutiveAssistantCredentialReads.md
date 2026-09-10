---
Name: Executive Assistant Credential Reads
Category: Architecture
Description: Why the EA's delegated-Graph seam is IObservable and not Task, how a hub turn deadlocks on a mesh read it awaits, and why "I could not determine your connection state" must never render as "you never connected".
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"/></svg>
---

# Executive Assistant Credential Reads

The Executive Assistant acts on a user's **own** mailbox and calendar with a **delegated** Microsoft
Graph token, minted on demand from a refresh token the user consented to and that we store encrypted
as an `EaCredential` node. Two very different callers ask for that token, and the difference between
them is the whole subject of this page.

| Caller | Where it runs | `async`/`await` there |
|---|---|---|
| `EaConsentController` (`/auth/ea/connect`, `/auth/ea/callback`) | an ASP.NET request thread | **sanctioned** — the signature is MVC's |
| The EA's mailbox **tools** (`MeshWeaver.Mail.MicrosoftGraph`) | inside an agent round, on a hub | **forbidden — it deadlocks** |

For a long time the seam between them, `IEaGraphAuth`, was `Task`-shaped, and the implementation's
own class comment recorded that as a design note: *"this sits at the OAuth/HTTP boundary (called from
the consent controller and the async EA tool), so `async` is appropriate here."* The second half of
that sentence was the bug, written down as if it were a decision.

## Why awaiting a mesh read deadlocks

A hub processes one turn at a time. `MessageService.DrainOne` **subscribes** to a turn and, when the
turn does not complete synchronously, **returns without dequeuing the next one** — the drain is
re-scheduled from that turn's terminal callback and from nowhere else.

So a caller whose work cannot finish until a cross-hub mesh read finishes is holding the very queue
that read's reply has to travel through:

```
turn N   ─┬─ starts the credential read ──────────────────► node hub
          │                                                    │
          └─ does not terminate: it is waiting for the reply    │
                                                                ▼
turn N+1 ── the reply ── QUEUED behind turn N, which is waiting for it
```

Nothing arrives. The read's own `.Timeout(...)` is the only thing that ever ends the wait — so the
symptom is a timeout, and the cause is not slowness.

`await` is simply the syntax that produces that turn shape. The hub's *own* handler surface is
already immune: `SyncDelivery` and `AsyncDelivery` both return `IObservable<IMessageDelivery>`, so an
`async` handler does not even compile. What `IEaGraphAuth`'s `Task<T>` did was reopen the door from
the other side — a `Task<T>` has exactly **one** consumption idiom, so a hub-side caller had no
choice but to await it.

> Reproduced, not argued: `EaCredentialReadTest.AwaitingTheCredentialReadInsideAHubTurn_NeverCompletes`
> seeds a credential node, reads it from a turn that waits for the read, and asserts a
> `TimeoutException` on a node that is demonstrably present. The sibling test issues the same read
> from the same turn by subscribing instead, and gets `Connected`.

## The seam is reactive; the controller bridges at its own edge

```csharp
public interface IEaGraphAuth
{
    IObservable<bool>          ExchangeAndStore(string code, string redirectUri, string userObjectId);
    IObservable<EaGraphAccess> GetAccessToken(string userObjectId);   // reads, then mints
    IObservable<EaGraphAccess> GetConnection(string userObjectId);    // reads only
}
```

`IObservable<T>` is the one shape **both** callers can consume, because only one of them can bridge:

- the **hub side** composes with `.Select`/`.SelectMany`/`.Timeout` and `.Subscribe(onNext, onError)`,
  and its turn returns immediately;
- the **controller** keeps its `Task<IActionResult>` — that signature is ASP.NET's, not ours — and
  bridges exactly once, at that edge, with
  [`ReactiveCompletion.ObserveCompletion`](../AsynchronousCalls). Never `.ToTask()`: it completes its
  `TaskCompletionSource` from inside the Rx pipeline, so the awaiter resumes *inline on the
  signalling thread*.

The Entra token POST — the one genuinely-async leaf — goes through
[`IIoPool`](../ControlledIoPooling), never `Observable.FromAsync`, which would run its prologue on
whatever thread subscribed (a hub turn, when the EA tool subscribes mid-round) with no concurrency
bound.

## Absent is not the same as failed

The read used to answer `(null, null)` for **both** "this user has no credential" and "the read did
not complete". `GetAccessTokenAsync` then returned `null`, and the EA showed the consent link. A user
whose grant was stored and valid was told to connect their mailbox again — and because pressing that
link re-consents something that was never revoked, it *appears to work*, which is why the defect
survived undiagnosed.

There were **three** sites that collapsed into that one value, and only the first is transient:

| # | The old code | Fires when | Evidence it left |
|---|---|---|---|
| 1 | `catch (Exception ex) { … return (null, null); }` | any read fault, the 10 s `Timeout` included | a Warning naming the user |
| 2 | `Safe(…)` → `catch { return null; }` | content is `JsonElement` and will not deserialize | **none** — no exception variable, no log |
| 3 | `_ => null` in a `node?.Content switch` | content is neither shape (the as-written `JsonObject` DOM; a same-named type from another collectible assembly) | none |

A fix touching only the two `catch`es leaves the third live. Site 3 is not an error handler at all —
it is a **hand-rolled shape test**, the exact pattern the house rule against casting an `object`
payload exists to prevent. `node.ContentAs<EaCredential>(hub.JsonSerializerOptions, logger)` removes
the arm outright and folds site 2 into the same call, so there is no switch left to have an arm.

The answer is modelled instead:

```csharp
public enum EaConnection { NotConnected, Connected, Undetermined }

public sealed record EaGraphAccess(
    EaConnection Connection, string? AccessToken = null, string? Diagnostic = null);
```

- **`NotConnected`** — the read completed and found no credential. This is the **only** state in
  which offering the consent link is truthful.
- **`Connected`** — a credential was read (and, for `GetAccessToken`, a token was minted from it).
- **`Undetermined`** — the read did not produce an answer: it timed out, the transport faulted, the
  stored content could not be interpreted, or Entra refused the redemption. **Nothing is known.**

`Undetermined` always carries a `Diagnostic`, because an undetermined state with nothing to say is
indistinguishable from the swallow it replaces.

### A grant is only as wide as its consent

`NotConnected` has a second cause besides an absent node, added 2026-09-10: a credential whose
stored `Scopes` differ from the build's `EaGraphAuth.Scopes`. The constant is the only writer of
that field, so any difference is a scope-set change — and Entra refuses to redeem a refresh token
for scopes the user never consented to (400 `invalid_grant`). Calling such a grant "connected" was
a lie every Teams call disproved, and it closed a loop nobody could leave: the plugin reported the
refused refresh as undetermined, the user clicked the reconnect link, and the consent controller —
reading the same credential as connected — bounced them back without ever showing the dialog.
Classified as `NotConnected`, the same value both hands the user the consent link and makes
`/auth/ea/connect` run consent; the diagnostic says the mailbox side is intact, so the plugin's
sentence is "reconnect once", never "you never connected". The token endpoint is not asked at all
(`AStaleGrant_IsNotOfferedToTheTokenEndpoint` counts the calls).

### What a caller must do with `Undetermined`

Say so. "I could not check your mailbox connection just now" is the honest sentence, and it is
neither of the other two:

- offering the consent link would be the #3433 lie, and
- refusing outright would be equally wrong, because the user may genuinely never have connected.

The consent **controller** takes the third path, and deliberately: only `Connected` skips the dialog.
An `Undetermined` read goes *through* consent, because re-consenting a live grant is harmless
(Microsoft re-issues) whereas skipping it on a guess strands a user who really has not connected. It
logs a Warning naming the diagnostic on the way past, so the guess is never silent.

## Why no guard caught this, and what does now

Every existing guard enforces an **idiom**, not the shape. `ObservableToTaskBridgeGuard` matches a
hand-rolled Rx→`Task` bridge and `.ToTask(`; the blocking-bridge guards match `.Result`/`.Wait()`;
`HandWovenGateRatchetGuard` matches gates. The defective line was
`await ws.GetMeshNodeStream(path).Take(1).Timeout(10s).FirstAsync().ObserveCompletion(…)` — a
*sanctioned* bridge, awaited from code an agent tool reaches — and every guard was green. That is why
the maintainer's word for it was **"again"**.

"Hub-reachable" is not a syntactic property, and a guard that simply banned `async` under `memex/`
would red the consent controller, which is legitimate. `HubReachableAsyncGuard` therefore picks two
properties that *are* syntactic:

1. **`await` of a mesh read or write**, in any production root. Whatever such a method is called
   from, the read's reply must be processed by a hub, and a caller that cannot finish until the read
   finishes holds that hub's queue. Two ASP.NET-boundary sites carry the shape and are seeded in
   `test/AwaitedMeshReadSites.allow`, which states the boundary for each.
2. **A `Task`/`ValueTask`-returning member on an interface in `MeshWeaver.Mesh.Contract`** — the
   cause of (1). That assembly is the SDK-free contract hub-side modules and agent tools compile
   against; a `Task` there does not permit an await, it requires one. Seeded in
   `test/TaskShapedMeshSeams.allow`.

Both rules were **watched red** on a reintroduced copy of the defective line and green with it
removed, and the planted-text assertions in that guard re-run that experiment on every CI run — a
matcher that silently stops seeing the shape fails there rather than reporting a clean tree. Both
lists of markers are load-bearing: a new mesh entry point, or a new identity for the contract
assembly, must be taught to the guard in the same change that introduces it.

## Retiring the old surface across two repositories

`IEaGraphAuth` still carries `ExchangeAndStoreAsync`, `GetAccessTokenAsync` and `IsConnectedAsync` as
default-implemented forwarders over the reactive members. They are hub-unsafe by construction and
nothing in this repository calls them. They exist for one merge window, because the dependency runs
the wrong way for a clean delete:

- `MeshWeaver.Plugins` compiles `-warnaserror` against a **checkout** of core at a pinned ref, so a
  core PR that deleted the members could not land before the Plugins PR that stops calling them;
- and that Plugins PR cannot compile before the reactive surface exists in core.

The forwarders break the cycle: core adds the reactive surface and removes nothing, Plugins migrates,
then core deletes the forwarders as a genuine deleting-half paired with that Plugins PR. They carry
no `[Obsolete]` for the same reason — the attribute would red the dependent's `main` the moment core
merged. The deprecation lives in `TaskShapedMeshSeams.allow` instead, where a ratchet that may only
shrink is what actually removes them, and where it cannot break a repository that has not migrated
yet.

## See also

- [Asynchronous Calls](../AsynchronousCalls) — the reactive patterns and the bridge rules
- [Controlled I/O Pooling](../ControlledIoPooling) — the one sanctioned async boundary
- [CQRS and Content Access](../CqrsAndContentAccess) — why a single node is read from its stream
- [Access Context Propagation](../AccessContextPropagation) — why the read runs under `RunAsSystem`
- [Executive Assistant](/Doc/AI/ExecutiveAssistant) — what the assistant does with the token
