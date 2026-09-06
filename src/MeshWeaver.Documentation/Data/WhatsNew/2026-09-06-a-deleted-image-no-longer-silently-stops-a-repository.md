---
Name: A deleted image no longer silently stops a repository
Category: Fix
Description: Housekeeping in the image registry could delete the exact build a repository was pinned to, and every check in that repository then stopped before it began — including the nightly run that would otherwise have said so. A daily sweep now names the missing build instead.
Icon: Delete
Order: -20260906
---

# A deleted image no longer silently stops a repository

Each content repository is tied to one specific, known-good build of the platform, so that two runs
of the same content give the same answer. Those builds live in an image registry, and the registry
does its own housekeeping: old builds are cleared out on a schedule to stop the storage growing
without limit.

The two arrangements disagreed. A repository is meant to stay on its chosen build for weeks — that
is the point of choosing it — while the housekeeping kept only the newest handful. The more often
the platform was rebuilt, the sooner a repository's chosen build was cleared away. Nothing checked
whether anything still depended on it.

On 5 September that is what happened. Three repositories stopped at the same moment, and the message
they gave was that an image could not be found — several layers below anything that names a cause.
One of them lost its nightly run as well, so the one thing that would normally report a problem was
the thing that had stopped.

It is also a failure that hides. Machines keep a local copy of an image they have already used, so
work carries on for a while after the build is gone, and only fails when something starts fresh. The
deletion and the breakage can be days apart, which is why nobody connects them.

A daily sweep now reads every repository's chosen build and asks the registry whether it is still
there. If one is missing, it says so — naming the repository, the setting and the build — the
morning it happens, rather than leaving it to be discovered by everything mysteriously stopping. The
sweep reports what it looked at as well as what it found, so "nothing wrong" and "nothing checked"
can be told apart.

The housekeeping itself is registry-side configuration and is being changed separately, so that a
build something still depends on is not deleted in the first place.
