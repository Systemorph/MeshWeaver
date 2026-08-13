---
Name: A crashed server no longer stalls the rebuild for ten minutes
Category: Fix
Description: One server rebuilds in-portal code for the whole group. If it crashed mid-rebuild, everyone waited out a ten-minute timer before anyone took over — even though the cluster already knew it was gone. Takeover is now immediate, and a busy server can no longer lose its turn by mistake.
Icon: Sparkle
Order: -20260813
---

# A crashed server no longer stalls the rebuild for ten minutes

Code that lives inside the portal is compiled by the portal itself, and after a platform update all of
it has to be rebuilt. When several servers run the portal together, exactly one of them does that
work and the others simply wait and pick up the results — otherwise they all compile the same things
at once, fight over the same disk, and pages start failing to open.

Choosing that one server worked. What did not work well was noticing when it stopped.

The others were watching a timestamp on a shared file. If the chosen server crashed halfway through,
nobody could tell the difference between "it died" and "it is busy", so they waited out a
ten-minute grace period before one of them took over — and until then, nothing was being rebuilt.
The reverse mistake was possible too: shared network drives can serve a cached timestamp, so a
perfectly healthy server could look stalled and have its turn taken away, putting two servers on the
same work — the exact situation the whole arrangement exists to prevent.

Both of those were guesses about something the system already knew for certain. Servers running the
portal together form a cluster, and that cluster tracks continuously which of its members are alive
— with its own health checks, and second opinions from other members before declaring anyone gone.
The chosen server now signs its claim in a way the cluster can recognise, so the question is asked of
the cluster rather than of a file's clock:

- the cluster says it is **gone** → another server takes over at once, with nothing to wait out;
- the cluster says it is **alive** → nobody takes over, no matter how old the timestamp looks;
- the cluster has **no answer** — a single-server install, a developer machine — → the old
  ten-minute rule still applies, which is exactly what it was always for.

The heartbeat itself was also moved from a file's clock into the file's contents, so a cached
timestamp on a network drive can no longer be mistaken for a server that stopped responding.
