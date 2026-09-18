---
Name: The check that compares a portal's configuration against what it is supposed to be works again
Category: Fix
Description: The daily comparison between a deployment's intended configuration and what it is actually running had been unable to complete for over a month; it runs again, and its first report found a rollout safety check switched off on two portals.
Icon: ShieldCheckmark
Order: -20260918
---

# The check that compares a portal's configuration against what it is supposed to be works again

A portal's configuration is written down — which features are on, how it reaches its database, how
long it is given to start up. A daily check compares that written-down intent against what each
portal is *actually* running, so that a setting changed by hand in an emergency, and then forgotten,
gets noticed rather than quietly becoming the truth.

That check had not been able to complete a single comparison for over a month. It needed a piece of
configuration that, for good security reasons, it is not allowed to see — so it stopped before it
ever compared anything. Every day it reported a failure, and because the failure never changed, it
stopped being read.

It now completes. It does its comparison using only the information it is permitted to hold, and
proves on every single run that the placeholder it substitutes for the part it may not see cannot
influence the answer — so a report that says "these settings match" means it, and a report that says
they differ is about the portal rather than about the check.

The first report it produced found something worth finding: on two portals, a safety check that is
supposed to hold a new version back until it has verified itself was not actually in force — switched
off on one, and correctly switched on but with nothing reading it on the other. Neither was visible
in any of the written-down configuration, which is precisely the kind of gap this comparison exists
to close. Both are now being tracked and fixed.

The standing result is also no longer just a red mark on a dashboard nobody opens: it is kept as a
single tracked item that updates itself, and closes when every portal matches its intended
configuration again.
