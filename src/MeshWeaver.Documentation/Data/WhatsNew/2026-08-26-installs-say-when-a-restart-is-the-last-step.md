---
Name: Installs say when a restart is the last step
Category: Fix
Description: A package that adds compiled code needs a restart before that code runs — the package card now says so instead of reading simply "Installed".
Icon: ArrowClockwise
Order: -20260826
---

# Installs say when a restart is the last step

Some packages ship compiled code alongside their content. That code is put in place immediately, but
it only starts running after the portal restarts — a deliberate design, since swapping code out from
under a running portal is not safe.

Until now nothing said so. The package card reported "Installed", the feature it promised was not
there, and there was no way to find out that a restart was the missing half. The only place the
information appeared was an operator health endpoint, which the person who installed the package
never sees.

The card now carries a "restart required to finish activating this package" note whenever this
portal has code in place that a restart would start running — and it appears the moment the install
finishes, not on some later visit. The note clears itself after the restart, because it is derived
from what this portal has actually loaded rather than remembered as a flag.

Two cases deliberately show nothing rather than a misleading prompt: when the portal cannot read its
own activation record (an operator problem, reported to operators, that a restart would not fix), and
when the code cannot be started by any restart at all (which needs a re-install, not waiting).
