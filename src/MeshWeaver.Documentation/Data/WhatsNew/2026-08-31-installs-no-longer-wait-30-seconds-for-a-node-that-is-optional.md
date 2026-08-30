---
Name: Installs no longer wait 30 seconds for a node that is optional
Category: Fix
Description: Every package install held for a full 30 seconds waiting on a cover grant its own code called optional, then reported success. The wait is now a 5-second deadlock detector that runs only where a grant is genuinely owed — and says so loudly when one never arrives.
Icon: TopSpeed
Order: -20260831
---

# Installs no longer wait 30 seconds for a node that is optional

After writing a package's nodes, the installer waited for the partition's **cover grant** — the
access node a gating node type writes to make a gated partition readable — so that an install which
returns really is readable. The wait's own documentation said that grant is optional: "a partition
whose node type does not gate never writes it".

Both halves were true at the same time, and together they made a deadlock that never went red. On
the shape the code itself called *normal* the awaited node was never written, so the query could
never emit and every install paid the whole 30-second budget before continuing — green. The only
trace was one Information line worded to cover both outcomes, so the healthy install and the wedged
one printed the same sentence.

It was measurable in the installer's own suite: five tests spent 181 seconds of a 336-second run
doing nothing at all, one 30-second stall per install and 60 for the test that installs twice. The
same code is the production install path, so a real mesh paid the same 30 seconds for every package
that does not gate.

## What changed

The installer no longer asks whether a node type gates — it cannot; that machinery lives outside the
framework, which is why the grant is addressed by a well-known path in the first place. It asks its
own decision instead. Establishing a package's declared access is a step the installer already owns,
and it already records what it wrote: a public policy, a scoped public grant, or — for a commercial
package — deliberately nothing at all. Only that last shape is owed a cover grant by anyone.

So the wait now runs exactly there, with a five-second budget rather than thirty, and a partition
the installer published itself is not waited on at all. When the grant does arrive it is visible in
milliseconds, which is what makes the smaller budget honest: by that point the hub is already warm
and the only outstanding work is a single access-table write.

And when it does not arrive, that is no longer indistinguishable from normal. It is reported as a
warning naming the missing path and what it costs — a gated partition that denies every viewer,
including on the page that would sell it — while still never failing an install whose content is
already committed.
