---
Name: Installed tools keep working after a platform update
Category: Fix
Description: Six platform contracts had moved to another assembly without leaving a redirect, so an installed tool built before the move would have stopped loading the next time the portal updated — the same failure that briefly took the MCP tools down.
Icon: PlugConnected
Order: -20260826
---

# Installed tools keep working after a platform update

An installed tool does not remember *what* a platform type is called. It
remembers the name **and** which platform component carries it. Move the type to
a different component and the tool is left asking for something that no longer
exists there — so it fails to load, all at once, the next time the portal
updates. Nothing warns about it beforehand: the tool's own source still compiles
against the new platform perfectly well, because source and binary are asking
different questions.

That is what briefly took every MCP tool call down earlier: `get`, `search`,
`create` and the rest all failed identically, for every external client, from a
change that had reviewed as a tidy refactor.

The fix for that one also produced a check that can be run over any span of
history — and running it back to the last release turned up six more contracts
that had moved the same way, untouched. Three of them are used by tools people
have installed today: the co-hosted Claude Code and Copilot harnesses, and the
provider-key protection the chat view relies on. Each was a working portal one
platform update away from the same outage, purely as a matter of which order
things happened to be rebuilt in.

All six now leave a redirect behind at the old address, so a tool built before
the move and a tool built after it both resolve to the same thing. Nothing needs
rebuilding, reinstalling, or updating in any particular order — which is the
whole point: the ordering stops mattering.
