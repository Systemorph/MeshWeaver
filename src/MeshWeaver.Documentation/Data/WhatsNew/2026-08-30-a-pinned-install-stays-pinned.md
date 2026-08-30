---
Name: A pinned install stays pinned across a restart
Category: Fix
Description: An install with Admin/UpdatePolicy = None no longer rolls itself to the newest image every time its pod restarts — the updater now waits until it has actually read the persisted policy instead of starting on the deployment's configured default.
Icon: ShieldKeyhole
Order: -20260830
---

# A pinned install stays pinned across a restart

Setting **Update policy → None** is how an operator freezes an install on the image it is running.
It did freeze the scheduled checks — and then the install rolled anyway, on every pod start.

`memex.meshweaver.cloud` had read `policy: None` all day on 2026-08-30 and still rolled twice, to
`ci.6664` at 11:54 UTC and `ci.6739` at 15:03 UTC. The policy node's own history recorded both: a
`Startup` pass that evaluated a candidate as though updates were enabled, then — seconds later — a
`PolicyChange` pass concluding *"updates are disabled on this install (Admin/UpdatePolicy = None)"*,
after the roll had already been issued.

## What was wrong

The updater's policy stream was **seeded with the deployment's configured default** (normally
`Continuous`) and only afterwards read the value an administrator had actually persisted. The
startup pass ran on that seed — a full evaluation: list the registry, choose a candidate, patch the
Deployment. The real policy arrived a moment later and correctly disabled everything, one step too
late. Every pod restart re-opened the window, so a restart for any reason — a node drain, an
eviction, a failed probe — rolled a deliberately pinned install.

## What changed

The updater no longer invents a policy. The node is still seeded with the configured default when
it does not exist yet, but the poller waits for a real read of `Admin/UpdatePolicy` before it
evaluates anything, and an absent node is no longer parsed as "roll to the newest tag". An install
now decides only under a policy it has read.

Nothing changes for an install that has never set a policy: the seeded default is what the read
returns, and the first check happens as before.
