---
Name: A one-second hiccup no longer breaks something until the next restart
Category: Fix
Description: Setup work that runs once — preparing a workspace's storage, opening a connection, loading a collection — used to remember a momentary failure forever. It now simply tries again the next time something needs it.
Icon: ArrowSync
Order: -20260813
---

# A one-second hiccup no longer breaks something until the next restart

Some jobs are meant to happen exactly once and then be shared: preparing the storage for a new
workspace, opening a connection to another portal, loading a collection of documents, checking
whether a tool is installed. The platform runs each of those a single time and hands the result to
everyone who asks afterwards, instead of repeating the work for every caller.

The flaw was in what "the result" meant. If the one attempt happened to fail — a database that was
busy for a second, a network blip, a service still starting up — the *failure* was remembered with
exactly the same permanence as a success would have been. Every later request was handed that same
old error back, instantly, without anything ever trying again. The database recovered a moment
later and it made no difference: the answer had already been decided, and only restarting fixed it.

The consequences were quietly severe. A new workspace whose storage preparation hit one bad second
could never be written to again — every save into it failed for as long as the portal stayed up. A
connection to another portal that failed its first handshake never reconnected. A document
collection that stumbled once while loading stayed empty. In each case the system looked broken in
a way that had nothing to do with the original, long-gone hiccup.

Now a successful result is still remembered and shared exactly as before — the work still happens
only once — but a *failed* attempt is forgotten. The next request that needs it starts a genuinely
fresh attempt, against a system that has probably recovered in the meantime.

Two things deliberately did not change. Nothing retries on its own: there is no timer quietly
hammering a service that is already struggling — the platform tries again only when something
actually asks for the result. And the failure is never hidden: whoever ran into the bad attempt
still sees the real error, so a genuine outage still looks like one instead of disappearing into a
silent retry.

This was fixed in the shared building block that all such one-time jobs are built from, rather than
in each of the eight places that had it, so anything written this way from now on gets the correct
behaviour automatically.
