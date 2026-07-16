---
Name: Compile retry now recovers types with a failed build
Category: What's New
Description: Retrying a compile (MCP compile/recycle tools) after fixing the source now reliably rebuilds a type whose previous compile failed — no more types stuck at Pending.
Icon: Sparkle
---

# Compile retry now recovers types with a failed build

When a node type's compile fails, the platform contains the failure so the broken type
cannot slow down the rest of the mesh. Previously, retrying the compile through the agent
tools (`compile` or `recycle`) after fixing the source could leave the type stuck at
"Pending" forever — the retry was silently swallowed, and even deleting and recreating the
type at the same path did not help.

Retries now go through the same release-request path as the Compile button, so a deliberate
retry always runs a real, fresh build. The compile tool also reports the result of *your*
build — never a leftover status from an earlier run — and deleting a type fully clears its
failed-build state so a recreate starts clean.
