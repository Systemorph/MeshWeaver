---
Name: Approvals and write conflicts show up in the activity feed
Category: Fix
Description: Approving or rejecting a request, and a resolved write conflict, now appear in the activity feed and the running-activities strip like every other activity.
Icon: History
Order: -20260813
---

# What's New — 13 August 2026

## Approvals and write conflicts show up in the activity feed

Approving or rejecting a request recorded an activity, but filed it under a path and a type that no activity view or query looked at. The record existed and was never seen: it did not appear in the document's activity feed or the running-activities strip, and opening it showed a bare node with no activity view. Resolved write conflicts had the same problem for the same reason.

Both now use the standard activity shape, so they appear alongside imports, syncs and script runs — and an approval's activity inherits the document's permissions, as a record attached to that document should. Existing records stay where they are.
