---
Name: Streaming answers cost far less to watch
Category: Fix
Description: While an agent types, the server no longer re-sends the whole answer-so-far to every viewer on every update — it sends the new words. A 20 kB reply went from 1.93 MB to 42 kB per viewer.
Icon: Flash
Order: -20260813
---

# Streaming answers cost far less to watch

While an agent writes an answer, its text is pushed out about ten times a second so you see it
appear. Each of those updates used to carry **the entire answer so far** — to every open view of
it, and to every server in the cluster mirroring that node. The tenth update re-sent ten chunks,
the hundredth re-sent a hundred, so the cost of one reply grew with the square of its length and
multiplied by the number of people watching.

An earlier change fixed the same problem on the way *in* — when an agent saves what it has written.
This is the other direction, the one that reaches you. An update now carries just the newly added
span, together with a fingerprint of the text it was computed against. Measured on a 20 kB answer
delivered over 200 updates: **1.93 MB → 42 kB per viewer**, and the cost per character of the answer
now *falls* as the answer grows instead of rising.

The fingerprint is what makes it safe. A viewer applies the shortened update only when it can prove
its copy is exactly the text the server started from; if anything has diverged it asks for a
complete refresh instead of guessing. So the text you end up with is byte-for-byte the text the
model wrote — never a partially-applied approximation of it.

Older clients are unaffected on purpose. Each connection says whether it understands the shorter
form, and one that does not keeps receiving exactly what it received before, so nothing changes
underneath a browser tab that has been open across an upgrade.
