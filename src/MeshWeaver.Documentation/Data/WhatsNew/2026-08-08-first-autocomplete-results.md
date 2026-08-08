---
Name: First "@" suggestions are now correct
Category: What's New
Description: The first autocomplete popup after typing "@" in the chat composer now shows the results for what you actually typed — no more re-triggering to get the right list.
Icon: Sparkle
---

# First "@" suggestions are now correct

Typing `@` in the chat composer to reference a mesh node sometimes showed the
wrong suggestions on the very first popup — typically the results for a previous
search, or for the bare `@` before you had finished typing the name. Only
deleting and retyping the reference (or otherwise re-triggering the popup)
brought up the right list.

The composer now tags every batch of streamed-in suggestions with the search
text it answers, and the suggestion widget only shows a batch that matches what
you are currently typing — anything stale is discarded and the correct search
runs instead. The first popup reflects your actual input, every time.
