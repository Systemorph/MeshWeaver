---
Name: Deleted data stays gone on the next read
Category: Fix
Description: A successful delete now waits until the shared read view has applied the deletion, so an immediate follow-up read cannot return the removed item.
Icon: Sparkle
Order: -20260910
---

# Deleted data stays gone on the next read

Deleting an item could report success just before the portal's shared read view had processed the
same change. An immediate follow-up read could therefore briefly return the item that had just been
deleted.

The delete response now waits for that read view to carry the item's absence. Once deletion reports
success, the next read agrees and the removed item stays gone.
