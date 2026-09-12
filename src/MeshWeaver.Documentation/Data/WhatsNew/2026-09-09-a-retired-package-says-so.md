---
Name: A retired package now says so
Category: Fix
Description: An instance whose install records still named withdrawn packages got no word about it from the update check — correctly not blocked, but also not told. Those records are now reported by name, with the remedy, while the check continues not to hold on them.
Icon: ShieldCheckmark
Order: -20260909
---

An install record names a package the instance expects to keep. When a package is withdrawn — as
the Agent and Skill packages were, deliberately, once the AI engine started serving their content —
the records naming it outlive it. Nothing publishes it any more, and nothing ever will.

The update check handled that correctly in the only way that matters: it did **not** wait. A release
cannot conjure a build nobody produces, so holding for one holds for ever.

## What was missing

It also said nothing. An operator whose records had gone stale got no signal from the check at all,
so the only place those package names still appeared was the record of a hold from days earlier —
which by then described rules that had since changed. The absence of a live answer is what sent
people to a dead one.

## What it does now

A required package is reported by name when the instance has never had it and the release does not
offer it: the record names a module, the release's sealed set does not carry it, and no copy of it is
installed here. The message says what it means and what to do — the record is what needs fixing,
either by removing it or by pointing it at the package's new home — and the check still does not
hold on it.

Neighbouring situations keep their own answers, because their remedies differ: something installed
that the release does not publish is measured against the release instead, and content awaiting a
bake still reports that it would be rebuilt at first start.

One case deliberately stays silent. If the release's module list could not be read, the package was
never looked for — and "we did not look" must not be reported as "it does not exist", least of all
when the thing that is broken is the ability to look.
