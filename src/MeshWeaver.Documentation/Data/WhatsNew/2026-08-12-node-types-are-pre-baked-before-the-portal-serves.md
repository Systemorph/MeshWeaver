---
Name: Node types are pre-baked before the portal serves
Category: Fix
Description: The startup pre-build now waits for shipped content to finish loading, so node types are compiled up front instead of on a user's first click.
Icon: Sparkle
Order: -20260812
---

# Node types are pre-baked before the portal serves

A portal compiles its node types once at startup so that opening a page later is instant. That
pre-build took its list of types before the content shipped with the release had finished loading,
which is a race: on one start the list was complete, on the next it was missing everything that had
not landed yet. Whatever was missing never got pre-built, and the first person to open one of those
pages waited for it to compile right then.

The pre-build now waits for the shipped content to finish loading before it takes its list, so the
whole set is compiled up front. If that content fails to load, the pre-build still runs and compiles
everything that did arrive rather than waiting forever.
