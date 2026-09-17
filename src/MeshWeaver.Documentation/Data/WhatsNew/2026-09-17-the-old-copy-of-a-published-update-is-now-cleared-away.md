---
Name: The old copy of a published update is cleared away
Category: Fix
Description: Each prepared plugin update was published twice — into its own folder, and over the top of a second copy kept for installations that predate the pointer. That second copy was the last thing two pipelines could overwrite at the same time, and leaving it behind would have frozen it in time. It is now removed once the new folder is live, its completeness marker first.
Icon: Broom
Order: -20260917
---

# The old copy of a published update is cleared away

When the platform prepares plugin content for an installation, it publishes a set of files that
every installation running that platform version then reads. Since the change that publishes each
update **whole and then points at it**, every update goes into its own folder, with a one-line
pointer saying which folder currently applies.

That change kept a **second copy** at the old location, for installations that predate the pointer
and would not know to follow it. The copy was written by overwriting whatever was there — which is
exactly what the folder-per-update change removed everywhere else. So one copy still had the old
problem: two pipelines publishing at the same time, one folder, and a run that goes red because it
found the other one's files in the middle of its own.

Every installation the platform can see has been able to follow the pointer for weeks, so the copy
has no readers left.

## What changes

**The copy is now removed, not rewritten.** Once the new folder is complete and the pointer has
been moved to it, the publisher deletes the old copy — its "this set is complete" marker **first**,
then its files.

The order is the whole of it. Removing the marker turns the old location from *"a complete, older
set"* into *"a set being republished"*, which every reader already waits out. Doing it the other way
round would hand a reader a set marked complete with files missing from under it.

**And it is deliberately not "stop writing it".** Leaving the copy behind, marked complete, while
the pointer moves on would freeze it on the day of this change: anything that ever fell back to it
would then be served a set that is self-consistent, complete-looking and older every hour. That is
worse than the problem being fixed, not better — so the copy goes.

## What this means for you

**Two updates published at the same time no longer make either one fail.** They write two different
folders and nothing else, so both succeed and the newer one ends up live. Until now the second copy
was the one thing they still shared, and the pipeline that lost the race went red on it.

**The pointer is read back before anything is deleted.** Storage has occasionally reported a write
as successful without storing it, so "the pointer says the new set is live" is now confirmed by
reading it, not assumed from the upload. If it did not land, nothing is removed and the run fails
saying so — the older set stays exactly where it is.

**A location nothing publishes to any more keeps its copy.** The clean-up happens per location, as
part of publishing there; there is no sweep. Old sets are removed later by the ordinary retention
rules, once nothing references them and they are more than a month old.

## One thing that reads differently now

A publication's pointer is a small file, and for the fraction of a second it is being replaced a
reader can catch it unreadable. That has always fallen back to the old location — which, from now
on, is empty rather than holding a complete set. Everything that *serves* content already treats
that as "come back in a moment". Everything that *decides* something from it — whether an
installation's content may advance, whether a release is complete enough to roll — now treats it as
**"cannot tell"** and holds, instead of as "there is nothing here", which would have quietly meant
"carry on".

The layout, the order the publisher works in, and what each phase of the migration did and did not
close: [Sealed Publication Generations](/Doc/Architecture/SealedPublicationGenerations).
