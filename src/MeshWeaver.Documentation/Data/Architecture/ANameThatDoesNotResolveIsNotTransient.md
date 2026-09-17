---
Name: A Name That Does Not Resolve Is Not Transient
Category: Architecture
Description: >-
  The standard resilience handler retried a hostname that does not exist, three times, logging each
  attempt at Error — so a URL in somebody's data manufactured a platform incident. Which socket error
  is permanent, which is genuinely transient, and why the breaker must not count it either.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M2 12h20"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/><path d="m4.9 4.9 14.2 14.2"/></svg>
---

# A Name That Does Not Resolve Is Not Transient

**A retry exists to give a *transient* failure another chance. A hostname that does not exist will
not start existing, so retrying it is not resilience — it is three guaranteed failures, three log
lines and a delayed answer.**

## What was measured

memex.systemorph.com, 2026-09-17
([#4613](https://github.com/Systemorph/MeshWeaver/issues/4613)). Two outbound fetches — an agent
reading a company's website through the WebSearch plugin's `FetchWebPage` tool — failed like this:

```
fail: Polly[3]
  Execution attempt. Source: '-standard//Standard-Retry', Result: 'Name or service not known
  (www.boss-software.ch:443)', Handled: 'True', Attempt: '3'
  System.Net.Http.HttpRequestException: Name or service not known (www.boss-software.ch:443)
   ---> System.Net.Sockets.SocketException (0xFFFDFFFF): Name or service not known
```

`Attempt: '3'` is the whole story: the standard predicate handles every `HttpRequestException`, so
each URL was tried three times.

## Whose defect this is — measured, not assumed

The issue offered three environmental explanations (cluster DNS, an egress `NetworkPolicy`, a pod
with no route). **All three are ruled out from outside the cluster.** Resolved from a laptop on the
public internet, 2026-09-17:

| name | answer |
|---|---|
| `www.boss-software.ch` | NXDOMAIN |
| `www.bosssw.ch` | NXDOMAIN |
| `boss-software.ch` | NXDOMAIN |
| `bosssw.ch` | NXDOMAIN |

Neither apex domain exists, so nothing inside the cluster is broken and "confirm whether those
hostnames resolve from inside the cluster" never needed asking. **Their half** is a URL in somebody's
data that points at a hostname that does not exist; no code change fixes that, and neither hostname
appears anywhere in `MeshWeaver`, `MeshWeaver.Plugins` or `Memex`, so nothing we ship put it there.

## Our half, and why it is worth fixing

Three attempts, each logged at `Error` under category `Polly` — and Error lines are what the fleet's
log watcher folds into a `LogIncident` and a GitHub ticket. **So a bad URL in customer data
manufactured a platform defect report.** The incident pipeline could not tell "our software is
broken" from "somebody typed a hostname that does not exist", and the second is not ours to fix.

`Memex.Portal.ServiceDefaults` therefore excludes a name-resolution failure from the default
pipeline's retry predicate **and from its circuit breaker**:

- **Retry** — no attempt can make a name resolve, so the second and third are pure cost.
- **Breaker** — the default pipeline is shared by every client that does not name its own, so
  counting a dead hostname as a failure lets one bad URL push the breaker toward open for calls that
  have nothing to do with it. A name that does not resolve says nothing about the health of any
  endpoint.

Excluding it from `ShouldHandle` also drops the log level: Polly logs an *unhandled* outcome far
below Error, so the incident stops being manufactured at its source rather than being filtered
downstream.

## 🚨 Exactly one socket error, and the control matters more than the case

`SocketError.HostNotFound` **only**.

`SocketError.TryAgain` (EAI_AGAIN) is a **nameserver that failed to answer** — genuinely transient,
and excluding it would turn a DNS hiccup into a hard failure, which is the opposite defect and a
worse one. `ConnectionRefused` is a host that exists and is not listening: about the *service*.
`ConnectionReset`, `TimedOut` and every TLS failure are about the network or the peer. All of them
keep being retried exactly as before.

`ServiceDefaults.NameDoesNotResolve` walks the inner-exception chain, because `HttpClient` wraps the
resolver's `SocketException` in an `HttpRequestException` and a handler pipeline can wrap that again.
It is pure, and `NonexistentHostIsNotRetriedTest` asserts it directly — a predicate that can only be
exercised through a real failed connection is a predicate nobody checks.

## The half this does not reach

**The caller is still not told in words it can act on.** `FetchWebPageCore` already draws the right
line for an HTTP *status* — a 404 is reported to the model as an answer rather than raised as a fault
— but a dead hostname still arrives as a raw `Name or service not known`, and the person who owns the
data that holds the URL learns nothing at all. That is a separate change in
`MeshWeaver.AI.WebSearch`, and the principle it should follow is the one already written at that call
site: a fact about the URL is an answer, not a malfunction.

## Related

- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — what a fault must
  surface as, and where swallowing one costs a wedge.
- [An Unreachable Store Is Not a Refusal](/Doc/Architecture/StoreUnreachableIsNotARefusal) — the same
  distinction one layer in: an availability failure is not a verdict.
