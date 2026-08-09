---
Name: New thread opens in the main view
Category: Fix
Description: Starting a conversation from the ✨ menu now opens the composer in the main view instead of the side panel, and the entry is marked with a language-neutral ➕.
Icon: Sparkle
Order: -20260804
---

# New thread opens in the main view

Picking **New thread** from the ✨ menu used to open the chat side panel. It now opens the composer
in the **main view** — the full-width surface — and closes the side panel on the way, so you are
never looking at two separate places to start a conversation.

This also fixes a case where the menu entry appeared to do nothing at all: when the side panel was
closed, the signal telling the composer to start fresh was sent before there was anything listening
for it, and it was simply lost. That path is gone.

The entry is now marked with a plain **➕** rather than a chat bubble. A plus reads as "new" in every
language and no longer looks identical to the **Threads** entry right below it.

On your home page, **My items** now sits above **Open threads**.
