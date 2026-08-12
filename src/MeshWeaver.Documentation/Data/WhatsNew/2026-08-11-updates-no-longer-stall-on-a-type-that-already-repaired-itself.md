---
Name: Platform updates no longer stall on a type that has already repaired itself
Category: Fix
Description: If a custom type fails to build because a package update was landing at that exact moment, the update now finishes as soon as the type builds again — instead of stopping until someone noticed.
Icon: Sparkle
Order: -20260811
---

# Platform updates no longer stall on a type that has already repaired itself

While a platform update is being prepared, every custom type is rebuilt, and a type that used to
work but no longer does will hold the update back. That safety-check is deliberate: it is what
keeps a bad version from reaching you, and it has caught real breakage.

But a type can also fail to build for a reason that has nothing to do with the new version. A
plugin update rewrites several files, and it does not rewrite them all in the same instant. If the
rebuild happens to run between two of those writes, it sees a half-changed set of files — one file
already updated, the files that use it not yet — and reports errors about code that, moments later,
no longer exists anywhere. It reads exactly like real damage: an error message, a line number, a
type that stopped working.

Until now that verdict was final. Seconds later the remaining files landed, the platform rebuilt
the affected types by itself, and everything was healthy again — but the update stayed stopped,
because nothing ever went back to look. It took someone noticing and restarting to get moving
again.

Now the platform keeps watching every type it held the update for. The moment such a type builds
successfully on the new version, its objection is withdrawn and the update carries on by itself.
Nothing is retried or timed out to make this happen — the type building is simply better evidence
than the earlier failure, and it wins.

A genuine problem is unaffected. A type that is really broken never builds, so nothing withdraws
its objection and the update stays held exactly as before. And when this does happen, the platform
now says so in the log while it waits: if a type's source files changed while it was being built,
the log names that as the likely cause instead of blaming the new version.
