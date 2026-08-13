---
Name: Two servers no longer run the same build twice
Category: Fix
Description: A build claim is now exclusive across every server sharing a database, not just within one cluster — so a rollout compiles the platform's dynamic content once instead of once per server.
Icon: Sparkle
Order: -20260813
---

# Two servers no longer run the same build twice

When a new version of the platform is deployed, every piece of dynamic content in it has to be
recompiled before any server may serve. One server is supposed to claim that work and the rest wait
for its result.

The claim held only within a single server cluster. Two servers that share a database but belong to
different clusters each kept their own view of the build, each concluded that nobody had claimed it,
and each wrote its claim — and neither was told it had lost the race, because the two writes were
indistinguishable to the database. On the last deployment that meant the whole content set, 268
items, was compiled twice: once by the dedicated build server that exists precisely to spare the
live server that cost, and once by the live server anyway.

Claims are now settled in the database itself, by a write that can only succeed for one claimant.
The server that loses is told so, skips the build, and waits for the winner's result — the behaviour
that was always intended when a build is already running elsewhere. The claim also moved onto a
record of its own, so that a server which never held it can no longer overwrite the holder's claim
when it saves its own copy of the build state.

Nothing about the build's output changes: it was never at risk, because compiled artefacts are
stored under a key derived from their content, so two servers producing the same build produced the
same bytes. What is saved is the duplicated work — and, on the live server during a rollout, the
memory it took.
