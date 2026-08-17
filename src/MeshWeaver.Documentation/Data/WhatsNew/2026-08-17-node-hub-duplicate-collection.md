---
Name: Pages no longer go dark when a node type fails to compile
Category: Fix
Description: A page whose node type could not be compiled could take its whole hub down; it now falls back to the error overlay as intended.
Icon: Sparkle
Order: -20260817
---

# Pages no longer go dark when a node type fails to compile

When a node type could not be compiled, the affected page was supposed to fall back to a
read-only "compilation error" view that still tells you what went wrong. Instead, the fallback
could take the whole node down: its hub never started, so the page — and everything else served
from that node — answered nothing at all. Which node was hit was effectively arbitrary; it was
whichever type happened to fail to compile first.

The fallback now applies the shared per-node setup exactly once instead of twice, so it comes up
as designed. And in the rare case where a genuine configuration clash does stop a node from
starting, the log now names the clashing collection, the node it happened on, and both pieces of
configuration that claimed it — instead of a single word with no context.
