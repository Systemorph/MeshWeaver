---
Name: A pod shutting down is no longer reported as an error
Category: Fix
Description: Hosted-hub creation answered null for two conditions that are nothing alike — a host going down, and a hub configuration that threw — and the Orleans grain reported both at fail level. Every pod rollout opened a ticket for a race in which nothing failed. The condition is now named where it is known.
Icon: Bug
Order: -20260904
---

# A pod shutting down is no longer reported as an error

Every red line a portal writes becomes an incident, and every distinct incident becomes a GitHub
issue. That is what makes red mean something — and it is why a line that is red *by accident* is
expensive: it costs a ticket, a triage round, and a little of the credibility of every other red
line on the page.

One of those lines fired on **every pod rollout**:

> `fail:` … *Hub construction returned no hub for `Admin/PlatformVersion` (NodeType: Markdown).
> Either the hub configuration threw — see the 'Failed to create hosted hub' entry logged
> immediately before this one, which carries the real exception — or hosted-hub creation is frozen
> because this host (or an ancestor) is disposing.*

Read it again: the message **names both possibilities and commits to neither**. That was not sloppy
writing. The grain genuinely could not tell — `GetHostedHub` answers `null` for both, and a null
carries no reason. So the sentence listed what it might have been and logged the whole thing red,
because one of the two really is a defect.

The one that kept happening was the other one. A node hub activates, the pod it lives on is
stopping, and the container the hub would be built from is torn down *underneath* the construction:
Autofac answers every resolution with `ObjectDisposedException`. Nothing failed. Nothing was
written. The next request re-activates the node on a live host — which is precisely what the design
intends, and the message even said so, in the half nobody could confirm.

## The reason now travels with the answer

Hosted-hub lookup returns the outcome alongside the hub: **available**, **absent**, **the host is
shutting down**, or **construction faulted**. The collection that owns the container is the one
place that can tell those apart, so that is where the classification is made — not guessed at by a
caller two layers away that cannot see a container at all.

And the shutdown case is *measured*, not assumed. When construction dies on an
`ObjectDisposedException`, the container is asked directly whether it can still resolve anything. A
live container that happens to throw that exception for its own reasons still reads as a fault; only
a container that is genuinely gone reads as a shutdown. If the check itself cannot answer, the
outcome stays loud — the conservative direction, by construction.

## What each condition now looks like

A host going down is stated as what it is, at debug level, with the evidence that made it benign:

> *Hub construction for `Admin/PlatformVersion` (NodeType: Markdown) was refused because this host
> (or an ancestor) is shutting down — an expected teardown race, not a fault. Nothing failed and
> nothing was written; the next access re-activates this node on a live host.*

A configuration that threw stays exactly as loud as before, and still points at the entry carrying
the real stack:

> `fail:` … *Hub construction FAILED for … the hub configuration threw. See the 'Failed to create
> hosted hub' entry logged immediately before this one, which carries the real exception and its
> stack.*

Downgrading a level is never licence to swallow an outcome, so both branches still do everything
they did: the failure cause is recorded for the caller, parked deliveries fail fast instead of
hanging, and the grain deactivates so the next access retries from scratch. An *unclassified*
outcome — one nothing has claimed — stays at fail level too. Only a **known** shutdown is quiet.

This is the same distinction `CancellationClassifier` already draws for cooperative cancellation: a
caller that went away, a pool that drained, a host that stopped. The level a call site picks is a
ticketing decision, not a verbosity knob.
