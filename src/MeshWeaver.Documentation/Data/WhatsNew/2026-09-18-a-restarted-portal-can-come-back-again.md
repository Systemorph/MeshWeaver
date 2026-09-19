---
Name: A restarted portal comes back instead of sitting at a blank page
Category: Fix
Description: One health check spent seven to ten seconds reading a shared file share on every single check, against a five-second budget — so a portal that restarted could never report itself ready and served nothing for as long as it took someone to notice. It now answers instantly, from a reading taken in the background.
Icon: HeartPulse
Order: -20260918
---

# A restarted portal comes back instead of sitting at a blank page

Every portal answers a small "are you ready?" question while it starts up, and the platform holds
the new copy out of service until it says yes. That question has a five-second budget: the answer is
asked for again and again, and a copy that never answers in time never enters service at all.

On 17 September one portal spent **27 minutes serving nothing**. It had not been given a bad
version, and nothing was wrong with it. Both of its copies had restarted, and neither could answer
the readiness question inside five seconds — so neither ever came back, and the site returned an
error page until a person intervened.

The reason was a single one of the checks behind that answer. It reports which optional features
this portal is supposed to have and whether they have arrived yet, and to do so it read the shared
file store where those features are delivered — **every time it was asked**. On a portal with a
large store that read takes seven to ten seconds. Every other check on the same endpoint finished in
under a millisecond.

An earlier fix stopped the read happening on *every* question and remembered the answer between
them. That left the two moments that actually matter still paying the full cost: the **first**
question a freshly restarted copy is ever asked — which is exactly the one that decides whether it
can come back — and the first question after a new feature is delivered, which is precisely when
someone is watching.

Now the reading is taken in the background, on the portal's own schedule, and the check simply reads
whatever the last reading said. It answers in microseconds instead of seconds, and the answer no
longer gets slower as the store grows.

The one thing it deliberately does **not** do is guess. In the moment before the first reading
exists, the check says so in as many words — and while it says so, it holds the new copy back rather
than waving it through. "I have not looked yet" and "I looked and everything is fine" are different
answers, and a restart that waits a few seconds for a real one is a great deal better than a portal
that enters service missing a feature nobody checked for.
