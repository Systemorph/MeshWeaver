---
Name: The shutdown log says why, and what went down with it
Category: Feature
Description: A teardown now records the reason its caller gave, and anything torn down alongside it says whose teardown took it. Naming only the asker left the two questions that actually decide an investigation — which of several look-alike callers this was, and why a component nobody asked about disappeared — unanswerable from the log.
Icon: PersonQuestionMark
Order: -20260910
---

Last week the line that opens a shutdown started naming **what asked for it**. Working through the
next occurrence showed that answer stops one question short, twice.

## "It asked itself" describes three different things

Several parts of the platform refresh themselves in place: when something is rebuilt, when a
published build supersedes the one in use, when a stale overlay heals. All three ask *themselves* to
shut down, so all three produced the same sentence — and the investigation this came from turned on
telling them apart. It spent six occurrences and four failed publishes doing it by hand, from
timings.

Each of them has always known why it was recycling. The request simply had nowhere to carry it, so
the reason stopped at the caller's own log and never reached the log of the thing going down. It
does now, and the opening line prints it.

**A caller that says nothing is reported as having said nothing.** The line reads *"reason not
stated by the caller"* rather than quietly leaving the field out — a blank reads to the next person
as "there was nothing to report", which is the one thing it must not mean.

## Anything torn down alongside now names the teardown that took it

When a component shuts down, everything it owns goes with it. Those pieces were shut down the
ordinary way, so their own opening line said what an ordinary shutdown says: *nothing asked over the
wire*. Indistinguishable from a routine, unrelated stop.

That is exactly where the original incident had to be read. A package was installing 145 files; the
component that owned them was torn down partway through; the pieces **it** owned went too, and the
work waiting on those pieces was left unanswered until the install gave up ten minutes later. A
reader lands on one of those pieces first — and it named nobody. The teardown that actually took it
was invisible from there.

Now each one says it went down with its owner, names the owner, **and carries the reason the owner
was torn down for** — passed along unchanged however deep the ownership runs, so the piece at the
bottom still names the event that started it. One line, instead of correlating two logs by
timestamp.

## What this does not do

It still does not stop a shutdown from landing in the middle of an install. That remains open. What
changes is that the next one can be read rather than reconstructed.
