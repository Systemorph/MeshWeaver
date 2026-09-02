---
Name: One bad moment no longer deafens a hub for good
Category: Fix
Description: A hub that started up during a cluster reshuffle could lose its cross-pod messaging permanently — silently, and for as long as it kept running. It now rides the reshuffle out.
Icon: ArrowSync
Order: -20260902
---

# One bad moment no longer deafens a hub for good

A portal runs as several replicas, and the pieces of it that hold your data announce themselves to
the rest of the cluster when they start. Part of that announcement is a subscription: *"send me
anything addressed to me, wherever it comes from."*

The cluster reorganises its address book every time a replica joins or leaves — every deploy, every
scale-up, every restart. A subscription being announced during that window can be handed to a
replica that has just been declared gone. That is a normal, seconds-long condition with an obvious
answer: ask again once the reshuffle settles.

The platform already knew how to ask again. It just could not recognise this particular way of being
told to. The retry it would have used only recognised the phrasings the cluster uses when *reading*
the address book, and this failure comes from *writing* to it — a different sentence, and one nothing
was looking for. So the piece gave up on its very first attempt and marked its own cross-replica
messaging **disabled for the rest of its life**.

What that looked like from the outside: everything worked for anyone whose request happened to land
on the same replica, and quietly did not for anyone else. Updates that never arrived, an import whose
progress never moved, a page waiting on data that was never going to come. Nothing failed loudly,
because from each replica's own point of view nothing was wrong.

**The condition is now recognised, so the retry that already existed actually runs** — the
subscription is re-announced against a settled address book, and the hub keeps its cross-replica
messaging. On our own cloud portal this had happened thirteen times over the preceding ten days,
across three deploys, every one of them a reshuffle that was over in seconds.
