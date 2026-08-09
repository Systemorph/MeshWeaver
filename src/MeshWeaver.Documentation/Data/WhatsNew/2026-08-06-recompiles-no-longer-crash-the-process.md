---
Name: Recompiling a node no longer risks crashing the process
Category: Fix
Description: Repeated node recompiles could leave the portal open to a hard crash; the type registry now cleans up after each one.
Icon: Sparkle
Order: -20260806
---

# Recompiling a node no longer risks crashing the process

Every time you recompile a code node, the platform loads a fresh copy of it and throws the old one
away. Until now the type registry kept pointing at the discarded copy. That had two costs: the old
copy could never be released, so memory crept up on a mesh where people edit and recompile a lot;
and once enough of them had piled up, an unrelated background operation could stumble over one and
take the whole process down — a hard crash with nothing in the logs to explain it.

The registry now drops a node's types the moment its old copy is discarded. Recompile as often as
you like; nothing accumulates, and there is no stale entry left for anything to trip over.
