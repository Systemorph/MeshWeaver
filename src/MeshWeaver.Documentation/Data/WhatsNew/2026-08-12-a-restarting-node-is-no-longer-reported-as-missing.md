---
Name: A restarting node is no longer reported as missing
Category: Fix
Description: Reading a node while it restarts now retries and answers correctly, instead of reporting that the node does not exist.
Icon: Sparkle
Order: -20260812
---

# A restarting node is no longer reported as missing

Nodes restart routinely — after their code is rebuilt, after a recycle, after an update. A read that
arrived during one of those restarts got an answer meaning "this node is restarting, ask again", but
the read turned that into the same answer it uses for "there is no such node". Whatever asked then
behaved as though the node had been deleted: a page reported missing content, a check concluded
something was gone when it was merely coming back.

Such a read now asks once more, which lands on the restarted node and gets a real answer. If the node
is genuinely gone, that second answer says so. And if it is still restarting, the reader is told that
plainly rather than being handed a wrong answer it cannot distinguish from deletion.
