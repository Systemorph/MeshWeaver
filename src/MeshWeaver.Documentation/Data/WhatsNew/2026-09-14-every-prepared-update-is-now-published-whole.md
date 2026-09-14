---
Name: Every prepared update is published whole, not only the platform's own
Category: Fix
Description: Publishing an update whole — into its own folder, pointed at once complete — was switched on for one of the six content pipelines and had to be switched on repository by repository for the rest. It is now the default, so all six publish that way and any pipeline added later does too.
Icon: Checkmark
Order: -20260914
---

# Every prepared update is published whole, not only the platform's own

An earlier change stopped the platform overwriting a prepared set of plugin content **in place**
while installations were reading it: each publication is now written into its own folder, checked
there, and only then pointed at. A one-line pointer says which folder applies, and it moves as the
very last step.

That change had a switch, and the switch was off by default. It was turned on for the platform's
own pipeline. The five other content pipelines — one per content repository — were each expected to
turn it on themselves.

## What was wrong with that

The switch being off by default is not a neutral starting position. It means **a pipeline that says
nothing keeps overwriting in place** — and that includes every content repository that will be
created in the future, none of which has any reason to know the switch exists.

The reason the switch existed at all was a real one: while some publishers could write the new
layout and others could not, a mixture of the two on one folder could leave readers quietly on
stale content. That reason is gone. A separate change made the **folder** decide the layout rather
than the publisher, so a publisher that has not been told about the new layout still follows one
that is already in use — the mixed state cannot be entered any more.

## What changes

**Publishing whole is now the default**, so all six content pipelines do it, and so will any added
later. Nothing had to change in the content repositories themselves: they all follow the platform's
shared pipeline, which is what carries the default.

Publishing the old way is still possible by asking for it explicitly, and asking for it on a folder
that has already moved does not move that folder back.

## What this does not change

**One copy is still replaced in place, and it is still the reason a publication can clash.** Beside
the new folder, every publication also refreshes a copy in the old flat layout, so an installation
too old to follow the pointer reads exactly what it read before. That copy is the part two
simultaneous publications can still contend over. Retiring it — once no installation needs it — is
what removes the last of the window, and it is tracked separately.
