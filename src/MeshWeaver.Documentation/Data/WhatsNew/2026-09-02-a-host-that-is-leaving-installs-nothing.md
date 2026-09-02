---
Name: A host that is already leaving installs nothing
Category: Fix
Description: A node's host that was stopped before it finished starting no longer installs its background machinery on the way out, which used to log a warning about a refused child on most portal starts.
Icon: Bug
Order: -20260902
---

# A host that is already leaving installs nothing

Some hosts live for a moment on purpose. When the portal starts, it briefly builds a host for each
node type it ships, only to learn what kind of content that type carries, and lets it go at once.
The same happens when an assistant checks a piece of content against a type's schema.

Starting a host is a short list of steps that runs shortly after it is created — subscribe to its
own node, install a helper or two, start a ticker. When such a moment-long host was let go
*before* that list had run, the list ran anyway, on a host that was already on its way out. Every
step then installed something that was dead on arrival, and one of them — creating a helper — was
refused with a warning: `Rejecting hosted hub creation … during disposal`. On a typical start the
warning appeared for most of those brief hosts; it meant nothing was wrong, and it was the one line
that could turn a clean-teardown check red.

**A host that has begun to leave now skips the rest of its start-up list.** Nothing is installed on
it, nothing is refused, and the teardown finishes exactly as before. Hosts that are staying start
exactly as they always did.
