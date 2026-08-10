---
Name: Your installed plugins are visible again
Category: Fix
Description: An instance set up before the current access model came up with its whole plugin baseline — the Store included — installed but invisible. It now repairs itself on the next start.
Icon: LockOpen
Order: -20260810
---

# Your installed plugins are visible again

If your portal was set up a while ago, you may have opened it to find almost
nothing there: no spaces to browse, no Store, and no obvious reason why. The
content was not missing. It was installed, sitting in the database, and locked.

Two things had gone wrong together.

Older portals were provisioned with a gate that made every page inside a plugin
private and pointed visitors at a subscribe page. That gate was replaced long ago
— packages now say for themselves whether they are public, and free ones are
published to everyone as they install. But the replacement only ever *created* the
new setting. It never corrected an old one, because overwriting a deliberate
access choice is exactly what it must not do. So a portal carrying the old gate
kept it, start after start, and nothing inside its plugins could be read.

The Store was worse off. Every plugin needs it, but nothing installed it: the
baseline installed the packages it was told to and quietly assumed anything they
required was already there. Without the Store there was no catalog page — so the
one screen that could have fixed the situation by hand was the one screen you
could not open.

Both are fixed, and the repair is automatic. On its next start your portal
recognises the old gate — a locked-down setting sitting next to the blanket
denials that only the old installer ever wrote — clears the denials, and publishes
the partition as the package always said it should be. Anything a selected package
requires is now installed alongside it, so the Store arrives with the plugins that
depend on it.

A package that ships its own access rules is still left completely alone. That is
the point of the distinction: the repair keys on the old installer's fingerprint,
not on "this looks private to me". A plugin you deliberately keep closed stays
closed.

You do not need to do anything. Open your portal after it next updates and the
spaces, the agents, the skills and the Store will be where you expect them.
