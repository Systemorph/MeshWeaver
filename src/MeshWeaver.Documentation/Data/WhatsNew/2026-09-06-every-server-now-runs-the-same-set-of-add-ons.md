---
Name: Every server now runs the same set of add-ons
Category: Fix
Description: Servers used to pick up add-on updates one file at a time, so two servers started minutes apart could end up running two different combinations — and a server started mid-update could end up running a combination nobody ever shipped. Updates now arrive as one complete set, and every server takes the same one.
Icon: ArrowSync
Order: -20260906
---

# Every server now runs the same set of add-ons

The portal runs on several servers at once, and add-ons update themselves in the background. Each
server picks up whatever has been published the next time it restarts — swapping code underneath a
running server is how you get half-updated behaviour, so waiting for a restart is deliberate.

What was not deliberate was **what** each server picked up.

An update run publishes add-ons one at a time, and each one became live the instant its own files
were written. So the answer to "what should I run?" changed continuously while an update was in
progress, and every server simply took whatever the answer happened to be at the moment it started.
Two servers restarted a few minutes apart got two different answers and then kept them, side by
side, until they both restarted again. On one production portal three servers of the same
deployment, running the same portal build, disagreed about **39 of 40** add-ons.

Worse was a server that started in the *middle* of an update: it got the new version of the add-ons
published so far and the old version of the rest — a combination no update run ever produced and
nothing was ever tested against. That is what made a page type that had been working for months
suddenly fail to build, with nothing about it having changed: it was built against a mixture that
existed for a few minutes on one server.

Now an update run publishes its add-ons as one **complete set**, once, when the whole run has
finished, and a starting server takes the set rather than reading the individual files. Two servers
starting at any two moments between two update runs therefore run exactly the same add-ons, and the
half-and-half combination cannot occur at all — a server that starts mid-run takes the previous
complete set, which is the one the other servers are already on.

Three things follow from that, and each is visible rather than assumed:

- **A server that has not restarted since the last update is still behind**, because that has not
  changed and cannot: code is swapped at restart. But it is now behind by exactly one update run
  rather than by some private mixture, and the portal says which set it is on and whether any server
  has taken the new one yet.
- **An update run that fails part-way changes nothing.** It never publishes a set, so no server ever
  takes a partial one. The add-ons whose files did arrive are listed by name as waiting for a
  complete run — instead of quietly running on some servers and not others.
- **Housekeeping knows about it.** The routine that reclaims disk space from superseded add-on
  versions now treats the set every server is running as in use, so it can never delete something
  underneath a running server.
