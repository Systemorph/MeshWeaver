---
Name: Losing one portal machine no longer has to mean an outage
Category: Fix
Description: A portal could only ever be described as a single machine, so losing it meant minutes of downtime while a replacement started. The deployment description can now say "run two", and new checks stop it quietly drifting back.
Icon: Sparkle
Order: -20260814
---

# Losing one portal machine no longer has to mean an outage

A portal runs on machines, and until now the description that creates them could only ever say
"one". That is fine right up until the moment the machine goes away — a routine upgrade of the
underlying hardware, a maintenance drain, a crash — and then there is nothing left serving. The
replacement has to start from cold, and starting a portal is not instant: it warms up its content
before it accepts anyone. For the minutes that takes, the site is simply down.

The unhappy part is that everything else needed to run two machines was already in place and had
been for a month. The settings that let two copies find each other and share one workspace were
configured; the rule that says "keep at least two running" was configured; the shared storage they
would both write to was configured. One line in the deployment description said "one machine", and
that line quietly won every argument. The setting that was supposed to raise the number was read by
nothing at all.

Two things changed. The description can now genuinely say how many copies to run, and when
automatic scaling is in charge it steps back and lets the scaler decide rather than resetting the
number on every update. And the rule that limits how many copies may be taken away at once is now
written so that it stays correct as the number of copies changes — the old form had to be re-tuned
by hand each time, and when it was not, it silently blocked every routine maintenance operation
instead of permitting one at a time.

The rest of the work is about not having to find this out the hard way again. A description that
contradicts itself — asking for two copies in one place and one in another — now fails the build
rather than being deployed and discovered later. And a separate check that compares what is
actually running against what was described has been taught to look at availability at all: how
many copies there are, whether anything is allowed to replace one, and whether automatic scaling
has been paused. It could not see any of that before, which is why it had nothing to say the day it
mattered.

None of this changes anything you do on a portal. It changes how long a portal is unavailable when
the machine underneath it goes away — from minutes, to nothing you would notice.
