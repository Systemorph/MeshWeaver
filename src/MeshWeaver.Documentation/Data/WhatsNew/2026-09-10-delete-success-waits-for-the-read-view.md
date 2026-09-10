---
Name: Saved changes appear on the next read
Category: Fix
Description: Successful updates and deletes now wait for the shared read view, so an immediate follow-up read no longer replays stale data.
Icon: Sparkle
Order: -20260910
---

# Saved changes appear on the next read

Updating or deleting an item could report success just before the portal's shared read view had
processed the same change. An immediate follow-up read could therefore return the previous value or
briefly return an item that had just been deleted.

Update and delete responses now wait for their matching change to reach that read view. Once the
operation reports success, the next read no longer replays the state from before the operation.
