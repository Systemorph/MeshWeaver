---
Name: Chatting from your own page no longer looks for a page inside itself
Category: Fix
Description: When a conversation was anchored to a top-level page — most often your own home page — asking the assistant about that page sent it hunting for a page of the same name nested inside it. That page cannot exist, so the lookup always failed.
Icon: Sparkle
Order: -20260814
---

# Chatting from your own page no longer looks for a page inside itself

When you start a conversation somewhere in the workspace, the assistant is told which page you are
on so it can read it and answer about it. Turning that "which page" into an actual lookup involves a
small judgement: a short name like `Notes` means *the page called Notes, here, next to the one I am
on*, while a longer address like `ACME/Reports/Notes` already says exactly where to look and should
be used as written.

The rule used to decide that by looking for a `/`. Anything without one was treated as a name
relative to the current page — which is right almost everywhere, and wrong in exactly one place: at
the very top of the workspace. A top-level page has no `/` in its address, so when the conversation
was anchored to one and the assistant asked to read *that same page*, the rule helpfully placed it
underneath itself and went looking for something like `yourname/yourname`.

No such page can ever exist. The lookup failed every time, the assistant carried on without the
content it had asked for, and nothing surfaced to say a read had been thrown away.

This affected people whose conversations start from a top-level page — most commonly your own home
page, which is why it showed up for some people and never for others. Conversations anchored deeper
in the workspace were never affected.

Reading the page a conversation is anchored to now resolves to that page, as written. Everything
else is unchanged: a short name still resolves next to the current page, an address that already
begins with the current page is left alone, and a sibling whose name merely starts with the same
letters — `ACMEArchive` alongside `ACME` — is still treated as a sibling rather than mistaken for
something already nested inside.
