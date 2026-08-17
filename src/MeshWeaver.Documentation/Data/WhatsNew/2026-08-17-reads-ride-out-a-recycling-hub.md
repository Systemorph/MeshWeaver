---
Name: Node reads ride out a recycling hub instead of failing
Category: Fix
Description: A node read that landed in a hub's recycle window burned its single re-probe within milliseconds and failed terminally with almost its whole budget unused — every NodeType compile reading its package root during an install-recycle settled as a phantom compile error. Reads now re-probe throughout their budget and deliver the node once the address reactivates.
Icon: ArrowSync
Order: -20260817
---

# Node reads ride out a recycling hub instead of failing

A mesh hub that is recycling — restarting after a package install, a recompile, a config
change — answers reads with an explicit "I am shutting down, ask again" verdict rather than
pretending the node is gone. The read primitive honoured only the first half of that contract:
it re-probed exactly once, immediately, so both probes landed within milliseconds of each other,
and any recycle longer than a beat became a terminal error with almost the caller's entire
budget unused.

The visible symptom was phantom compile failures. When installing a package recycles its root
hub, every one of the package's NodeType compiles reads that root; each read that landed in the
recycle window failed, and each failure was stamped as a compile error — parking whole modules
on the compilation-error overlay in the portal, and turning satellite-repo CI gates red on
types whose code was perfectly fine, on a different module every run.

Reads now keep re-probing on a paced schedule for as long as their own budget allows, and
deliver the node as soon as the address reactivates. A hub that recycles for the entire budget
surfaces a dedicated recycling verdict — which the compile pipeline files as "availability
unknown, retry", never as a verdict about the code.
