---
Name: Concurrent node updates keep their place in the queue
Category: Fix
Description: Updates arriving together now share one queue, and pending writes receive an explicit result when that queue shuts down.
Icon: CheckCircle
Order: -20260909
---

# Concurrent node updates keep their place in the queue

Two updates arriving together for a node could each create a queue. Replacing one queue could
remove its subscriber while a caller still held it, leaving that caller waiting for a result.

Concurrent callers now share the same queue. Accepted writes keep it alive until their work
settles, and shutdown reports an error to pending callers instead of leaving them waiting.

The [investigation and ownership rules](/Doc/Architecture/UpdateQueueOwnership) distinguish the
reproduced defect from the intermittent CI timeout that prompted the investigation.
