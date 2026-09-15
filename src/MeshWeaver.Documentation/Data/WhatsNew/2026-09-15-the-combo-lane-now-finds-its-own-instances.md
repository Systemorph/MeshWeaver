---
Name: The combo lane now finds its own instances
Category: Fix
Description: Which portals the combo verification covers is derived from the fleet's own deployment overlays instead of a hand-maintained list in a repository variable - a list that was already one installation short of the fleet, and that nobody could have noticed was short.
Icon: Sparkle
Order: -20260915
---

# The combo lane now finds its own instances

Before a portal applies a self-update, it consults a recorded verdict: *can that image actually
serve the modules this instance runs?* An off-cluster lane produces those verdicts, one per
instance — and until now it was told which instances existed by a JSON list typed into a repository
variable.

That list is a pin, and the platform has a rule against pins for a reason that this one illustrates
exactly: it fails in the direction nobody can see. An installation added to the fleet is simply
absent from it, so the lane goes green having verified everyone it was told about and nobody it was
not. Measured against the live fleet, the value that variable was specified with named **two**
portals. The fleet's deployment overlays declare **four** live ones, across three repositories —
and two of those four carry the same name, which the derivation now refuses rather than papers over
(see below).

## What changed

The lane now derives its roster from those overlays — the same two keys (*which installation is
this?* and *where does it answer?*) that the nightly image-retention lane already reads to decide
what it must protect. It reads them **through that lane's own extractor** rather than a second copy,
so the set that gets verified and the set that gets protected cannot drift apart. The one file that
can remove an installation still exists, and can only do it by declaring that installation retired
or not-yet-installed, with a reason — so forgetting an entry makes the lane ask for more, never
less.

## The failure it refuses to reproduce

An empty roster would produce an empty job matrix, an empty matrix skips the verification job, and a
skipped job is painted the same colour as a passed one. A *derived* zero paints exactly the green a
*declared* zero did, so the derivation exits red — naming the cause — on every route to an empty
answer: a repository whose overlays could not be read, an ambiguous or duplicated declaration, an
installation with nowhere to reach it, a stale exemption, or a fleet that genuinely declares no
portals. It also drives its own extractor over two known overlays on every run, so "the reader
stopped matching" can never arrive wearing "the fleet has no installations". Three layers refuse a
zero, and every one of them is exercised on every pull request.

## Two portals, one name

A portal's identity is qualified by the repository that declares it, so two deployment repositories
may each declare an installation called `memex` — and two of them do. That is correct where the
fleet asks each portal what it is running. It is not correct here: the per-instance credentials are
keyed by the name, so the two would be handed the same key and one's verdict would be recorded on
the other. The derivation refuses, naming both declaring files, and the fix is a decision — rename
one, or key the credentials by the qualified name — rather than something a workflow can paper over.

## What is still owed

The roster is derived; the credentials are not, and cannot be. Each instance still needs a key and
an admin token issued **at that instance** — an instance-registry key is stored as a hash and can
never be read back, so there is nothing anywhere to derive it from — plus the source-repository map
that only the plugin registry holds. Until those are provisioned the lane stays red and says, per
instance, exactly which one is missing. That is a provisioning task, and the lane naming it is the
lane working.

Full detail, the measured roster and the provisioning table are under Combo Gate Wiring.
