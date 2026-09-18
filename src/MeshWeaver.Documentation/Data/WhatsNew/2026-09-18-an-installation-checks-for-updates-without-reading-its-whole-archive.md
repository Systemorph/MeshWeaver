---
Name: An installation checks for updates without re-reading its whole archive
Category: Fix
Description: Before deciding whether a new platform version is safe to take, an installation read every build artifact it has ever stored — and that reading has to finish inside a minute. The archive only grows, so the check eventually stopped finishing, and the answer to "I could not look" is to stay where you are. The public portal sat six days behind while new versions kept being published.
Icon: Bug
Order: -20260918
---

# An installation checks for updates without re-reading its whole archive

An installation does not take a new version of the platform just because one exists. It first asks a
safety question: **does that version ship everything this installation has installed?** A portal that
rolled onto a version missing one of its packages would come up with that package broken, so the check
is deliberately cautious — and when it *cannot* answer, it holds. "I could not look" is never read as
"go ahead".

Answering that question needs to know which packages are supposed to ship content at all, and the
installation works that out from its own archive of published build artifacts: **every version it has
ever stored, not just the one it is being offered.** That is on purpose. Asking only about the version
on offer would mean a package whose build quietly stopped being produced would also quietly stop being
asked about — the check would go green about precisely the thing that had broken.

**But the archive only ever grows.** A new set of build artifacts is published roughly every half hour,
each under its own folder, and nothing removes the old ones. Re-reading all of them, on shared network
storage, is slower every day — while the safety question still has to be answered within a minute, or
the installation holds.

## What that looked like

It stopped finishing. Every check timed out, every new version was held, and the hold said so:

> the artifact catalogue for release 3.0.0-ci.8931 could not be read (the availability check did not
> answer within 60s)

Because that reads as a *compatibility* refusal rather than "the check ran out of time", it is easy to
look at and conclude the new version is genuinely unsafe. It was not. The public portal
`memex.meshweaver.cloud` stayed on a six-day-old build this way while fresh versions kept being
published every half hour, and several of its Store pages failed to compile against that older build
as a result.

## What changed

**The installation now remembers what each archived folder said**, and reads only the folders it has
not seen before. Each check still lists the archive — so a newly published set is always noticed, and a
removed one always drops out — but it no longer opens every folder it has already read. In a local
measurement over 800 archived sets, the repeated reading went from roughly a third of a second to three
milliseconds, and it no longer grows with the size of the archive.

**Nothing became less careful.** If anything cannot be read, the check still refuses to answer and the
installation still holds; that refusal is never remembered, so one momentary storage glitch cannot
freeze an installation until it restarts. Nothing about the *offered* version is remembered either —
its own artifacts are re-read in full, every time. And the one way the remembered reading can be wrong
is by keeping a package on the list a little longer than strictly necessary, which can only ever make
the check hold, never let something through.

There is also now a setting, **`SelfUpdate__AvailabilityAnswerBudget`**, for how long the check may
take — still one minute by default. It is there for an installation on genuinely slow storage. It is
not the fix for this, and raising it would not have been: on an archive that only grows, a bigger
budget buys a longer wait before the same hold.

## What to do

Nothing. An installation that was holding on this timeout resumes checking normally on the next
version it is offered after taking this release.
