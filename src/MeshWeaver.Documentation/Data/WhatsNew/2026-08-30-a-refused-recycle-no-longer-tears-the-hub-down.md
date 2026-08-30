---
Name: A refused recycle no longer tears the hub down anyway
Category: Fix
Description: Recycling a NodeType stamps a release request and then disposes its hub. When the stamp was refused for lack of permission, the dispose ran regardless — so a caller who was not allowed to change anything still got the destructive half. It now refuses outright.
Icon: ShieldError
Order: -20260830
---

# A refused recycle no longer tears the hub down anyway

Recycling a NodeType is two halves of one operation: stamp a release request on the node, then
dispose its hub so the next activation acts on that stamp. The stamp goes through the access
pipeline. The dispose does not.

The stamp's failure handler treated every failure as transient — reasonable for a timeout, and the
hub bounce is genuinely still worth doing there. But a **denial** is not a transient fault. On
2026-08-30 an operator without write access to a GitSynced module space asked for a recycle; the
access pipeline correctly refused the stamp, and the handler logged *"disposing the hub anyway"* and
tore the hub down. The NodeType was left with no release request to act on, and its watcher went
quiet.

So the one caller who should have changed nothing changed the only thing that mattered.

A refused stamp now refuses the whole recycle. Nothing is disposed, and the caller is told plainly
that they lacked permission — which leaves them exactly where they started, the correct outcome for
an operation they were never allowed to perform. A stamp that fails for any other reason still
proceeds, because that caller *was* allowed to ask.

## Two layers, and why both are needed

Recycle already checks permission before it starts, and answers with a legible reason. That check
is the first layer, and this change pins it with a test — the operator in the incident received a
*settle-timeout* instead, and could not tell "the trigger never dispatched" from "you were refused".

The incident proves the first layer is not enough: the operator got past it, and the **owner** of
the node refused the write. The owner's answer is the authoritative one and it arrives after the
local check has already said yes. Refusing on that answer too is what actually closes the hole.
