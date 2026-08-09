---
Name: Client text no longer lags behind the server
Category: What's New
Description: The JavaScript clients bundle a copy of the portal's text, and that copy had fallen behind. It is current again, and the check that keeps the two in step now runs whenever either side changes.
Icon: Globe
---

# Client text no longer lags behind the server

Every user-visible string is written once, on the server. The JavaScript clients carry a copy of
that text so they can put words on screen without waiting for a round trip. A check compares the
two and fails if they ever disagree.

The check only ran when someone edited the client. A string added on the server alone changed
nothing under the client folder, so the check never ran, and the copy silently fell behind — by
several dozen strings, including a sentence that had been reworded on the server and never
updated in the copy. Nothing warned about it, because the one thing that would have warned was
the thing that never ran.

Both copies are current again, in English and in German. More importantly the check now also runs
when the server's text changes, so the two are compared whenever either side moves — the change
that introduces a gap is the one that has to close it, instead of the gap surfacing later on
somebody else's unrelated work.

The same blind spot applied to the checks that keep the clients' control set and wire format in
step with the server. Those run on both sides now too, and a further check fails if a client ever
starts reading a server file that this arrangement does not yet cover.
