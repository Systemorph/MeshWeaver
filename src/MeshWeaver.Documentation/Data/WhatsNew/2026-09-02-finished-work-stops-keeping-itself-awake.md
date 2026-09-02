---
Name: Finished work stops keeping itself awake
Category: Fix
Description: Fixed a leak where a finished compile — or any other completed activity — could go on pinging its own node every 45 seconds for as long as the portal ran, so memory climbed with every burst of compiles.
Icon: Sparkle
Order: -20260902
---

# Finished work stops keeping itself awake

When something in the portal reads or writes a node, it keeps a warm connection to that node for a
short while, because whatever just touched it will usually touch it again. That connection sends a
small keep-alive ping every 45 seconds so the node stays responsive while it is in use. When nothing
has used it for a while, the connection is closed and the pings stop — and when the work it belonged
to has visibly finished, it is closed straight away rather than waiting.

A finished compile was not being closed by either route. A compile that succeeded in about a second
was found still being pinged **49 minutes later**, and it would have gone on doing so for as long as
the portal was running. Each such leftover keeps a node and its helper connections alive, so a burst
of compiles — a hundred in a quarter of an hour is normal when a space is being reimported — left a
hundred of them behind. Memory climbed, and on a portal already under pressure the next allocation
was the one that failed.

The cause was a bookkeeping gap. If a node was briefly unreachable while being read — a momentary
blip, not a real error — the portal remembered the failure so the next read could start fresh. When
it later cleared that memory, it dropped its own record of the warm connection but not the
connection itself. Both of the routines that close idle connections work from that record, so from
that moment neither could see it, and the ping had nothing left to switch it off.

Clearing the record now closes the connection with it, using the same shared routine the other three
paths already used. Nothing else changes: a node someone is actively watching is still left alone,
and work that is genuinely in progress still gets its keep-alive.
