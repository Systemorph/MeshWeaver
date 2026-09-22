---
Name: A hub teardown now says what caused it
Category: Fix
Description: When the platform tears down a running piece of itself and discards work that was queued behind it, it writes a report naming the work, who sent it and what it was waiting for - and then could not say which teardown threw it away. The most common cause knew the answer and had nowhere to put it. It does now.
Icon: Bug
Order: -20260920
---

# A hub teardown now says what caused it

The platform runs a small live component for each thing you are working with. Those components are
stopped and rebuilt constantly - after an update, when something needs rebuilding from fresh source,
or simply because nothing has touched it for a while and the space is better used elsewhere.

If work was queued behind one when it stopped, the platform writes a report. That report is careful:
it names the message that was discarded, who sent it, and which startup step it was still waiting
for. It is the report that becomes a bug ticket when it happens for a bad reason.

It ended by asking what had caused the teardown - because it genuinely did not know.

## Why it did not know

Teardowns arrive two ways. Most are *requested*, and a request carries a reason, so the report can
quote it. But a component can also simply be stopped outright, with nothing to carry a reason at
all. Those reported themselves as, in effect, "nobody asked" - honest, and no use to anyone reading
the ticket.

The largest single source of those is the most ordinary one there is: the platform reclaiming a
component that has gone idle. It knows exactly why it is doing so, and records that reason one line
earlier in its own log - a line written at a level the ticket pipeline never collects. So the answer
existed, a few microseconds and one log line away from the question, and never met it.

Measured before the fix, on one recorded incident: 364 occurrences across a week and thirteen
machines, every one naming the discarded work and its sender, and not one naming the cause.

## What changed

Anything that stops a component directly can now say who it is and why, and the teardown report
carries it. The ordinary idle-reclaim case now does exactly that.

Two things were deliberately kept. A stopper that genuinely has no reason still reports as having
stated none, rather than leaving a blank that reads as nothing to report. And where a component was
already asked to restart by name and the reclaim merely caught up with it afterwards, the original
request keeps the attribution - the first cause is the real one, and a later arrival does not get to
overwrite it.
