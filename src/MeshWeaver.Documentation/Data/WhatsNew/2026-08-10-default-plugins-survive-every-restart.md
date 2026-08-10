---
Name: Default plugins survive every restart
Category: Fix
Description: After an update, a portal could come back up with none of its standard plugins — and repeat that on every restart. The install now waits its turn and lands.
Icon: BoxCheckmark
Order: -20260810
---

# Default plugins survive every restart

Every portal ships with a standard set of plugins — the agents, the store, the
publishing tools, the collaboration features. They are installed automatically
each time the portal starts, so an update can never leave you without them.

Except that for a while, it could — and did. After an update, the portal came
back up with none of them. Not one. And because the same thing happened on the
next restart, and the next, it stayed that way: a running, healthy-looking
portal with its entire standard plugin set missing.

Two boot-time jobs were racing each other. When the portal starts on a new
version, it rebuilds every piece of dynamically-compiled code it hosts — a
sweep that takes several minutes. The plugin installer started at the same
moment, and each plugin it tried to install needed exactly the thing the sweep
had not gotten to yet: the plugin's own page type, freshly rebuilt. The
installer waited its allotted half minute, gave up, moved to the next plugin,
and lost the same race again — six times in a row, every boot.

The installer now simply waits for the rebuild sweep to finish before it
starts. On a portal that does not run the sweep, nothing changes — the install
starts immediately, as it always has. On the portals that do, the plugins
install a few minutes into the boot, once the ground they land on actually
exists.

As part of tracing this, the network calls the installer makes to the plugin
registry now identify themselves in the logs, so a slow or failed download
names its caller instead of appearing as an anonymous timeout.
