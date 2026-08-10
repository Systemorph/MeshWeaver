---
Name: An installation can now state exactly what it is running
Category: Feature
Description: One read lists every module an installation carries, where it came from and the exact version it was last built from — the input a release check needs before an update is offered.
Icon: Sparkle
Order: -20260810
---

# An installation can now state exactly what it is running

Before an update is offered to your installation, the safe question to ask is narrow and specific:
does this new version work with **the modules this installation actually has, at the versions it
actually has them**? Nobody can answer that centrally — a module that breaks on an installation
nobody uses is not an incident, and the same break on yours is.

Answering it needed something that did not exist: a single, reliable statement of what an
installation is running. Modules arrive two different ways — most are kept in step with a repository,
some are installed from the catalog — and each way records its version in its own place. Anything
that looked at only one of them reported almost nothing on a real installation and, worse, reported
it as a clean, empty result. That looks exactly like "nothing to check".

Now one read returns the whole picture: every module, which repository or package it came from, and
the exact commit or version it was last built from — both kinds folded into one list, so neither can
go missing.

It is also honest about what it does not know. A recorded version says where a module's content came
from, not that nothing has been edited since — pages you write yourself are yours, and the answer
says so rather than implying the two are identical. A module tracked only by a branch name is flagged
too, because a branch moves and a check against it would not mean the same thing twice. And if any
part of the picture could not be read, the result says it is incomplete instead of quietly coming
back short.
