---
Name: A blip reaching a hub no longer drops the message
Category: Fix
Description: A message aimed at a hub whose server briefly restarted or reconnected used to be reported as failed immediately; it is now retried so it lands once the target is reachable again.
Icon: ArrowSync
Order: -20260826
---

# A blip reaching a hub no longer drops the message

Servers roll over routinely — a new version ships, a pod restarts, a connection briefly drops.
Most of the platform already treats that as ordinary: a message aimed at a hub going through that
moment is retried a few times, and once the hub is reachable again the message lands as if nothing
happened.

One delivery path did not get that treatment. Messages routed to a hub that lives in its own
process — a portal session, a cache, a background sync — were given exactly one attempt. If that
attempt landed during the brief window a server was reconnecting or finishing its own restart, the
sender was told immediately that the message had failed, even though a moment later the very same
delivery would have succeeded.

Now that path retries too, the same way every other delivery in the platform does: a few attempts
with a short, increasing pause between them, before giving up and reporting a real failure. A
passing hiccup is absorbed instead of surfacing as a dropped message, and only a destination that is
genuinely gone still gets a prompt, honest failure.
