---
Name: Cleanup refuses when it cannot see every consumer
Category: Fix
Description: Artifact retention now derives its protection set from what the fleet is actually running rather than from committed pins, locks the tag as well as the manifest, and refuses outright when an installation did not report — instead of quietly protecting less.
Icon: ShieldLock
Order: -20260912
---

# Cleanup refuses when it cannot see every consumer

Retention decides what to delete by first working out what is still in use. The whole of this change
is one sentence about that step: **a protection set derived from a stale or incomplete source
deletes something that is still in use**, and a cleanup that cannot see every consumer must refuse
rather than protect fewer things.

That is not hypothetical. On the morning this landed, a production portal had been rolled onto
`3.0.0-ci.8399` while the committed pin that drives protection still read `8372`, so the nightly job
protected the image the portal was **not** running — measured unlocked, in a repository a nightly
task purges after seven days.

## What changed

**Protection now asks the installations, not only the files.** Each one is asked what it is running
through its own `/api/version`, and the image sets built from that commit are protected in every
repository its overlay pins — the portal and the migration image it is derived from together, since
half a set is a broken deploy. A committed pin is still read; it is now a second opinion rather than
the only one.

**The reference is protected, not only the bytes.** A container tag carries its own deletion flag,
separate from the image it points at, and it is the one a purge reads when it removes a tag. All
1,396 tags of the portal repository were unprotected, including the two tags both production
portals pin — so a purge would have left the image intact and the name pointing at nothing, which
fails a pull exactly as if the image had been deleted. Both are now locked.

**And protecting a multi-architecture image now protects the architectures.** A multi-arch image is
an index pointing at one manifest per platform, and the cleanup tool skips a protected index before
it ever looks at what the index points to — so the parts are judged on their own, and once they lose
the tags they were published with they are collected out from under it. The result is a protected
image reference that fails to pull. Worse, an *unprotected* index does get walked, so protecting
only the top-level reference made its parts less safe than leaving it alone. The whole set is
protected now, and a set that cannot be read is a refusal rather than an empty one.

**An installation that does not answer stops the job instead of shrinking the answer.** It is named,
the run reports its denominator — how many installations were expected, how many answered, how many
images and tags were protected — and the arm that releases protection is refused. An installation
that is genuinely gone is declared in a committed file with a reason; silence is never read as
retirement, because a portal that is down and a portal that was decommissioned look identical.

**The same rule now holds inside the portal.** The sweep that prunes the prebuilt-bundle store
already refused a report that admitted it was incomplete. It now also refuses when an expected
installation never reported at all, or when its newest report is more than a day old — a report
describing what an instance ran last week protects that, and leaves what it is running today
unnamed by anybody.

## Why not simply keep things for longer

Because that moves the cliff rather than removing it. A longer window, a larger keep count, a retry
around the step that collects the protection set — each one makes the same failure rarer and no less
certain, and a pin held stable for a quarter walks off the new edge just the same. The question was
never how much headroom to add; it was why a protection set was allowed to be derived from something
that no longer described the fleet.

Full detail: [ArtifactRetentionInterlock](/Doc/Architecture/ArtifactRetentionInterlock).
