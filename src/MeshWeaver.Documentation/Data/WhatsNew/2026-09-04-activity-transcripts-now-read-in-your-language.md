---
Name: Activity transcripts now read in your language
Category: Fix
Description: Import, compile, delete and write-conflict activity logs were stored as English sentences and rendered in English to every viewer; the lines the platform writes itself are now translated for whoever opens them.
Icon: Sparkle
Order: -20260904
---

# Activity transcripts now read in your language

When the platform does a piece of work for you — importing a Space from GitHub, compiling a node
type, deleting a subtree, resolving two edits that landed at once — it keeps a running transcript you
can open afterwards. Those transcripts were written in English and stayed English, whatever language
you use the portal in.

That was not an oversight at 53 separate places. A transcript line is written the moment the work
happens, on the server, with nobody looking at it yet — and the same stored line is later read by
different people in different languages. There was nowhere for a translation to happen, because by
the time you opened the activity the sentence had already been decided.

Lines now carry what they *mean* rather than a finished English sentence, and the translation happens
when you open the activity — so two people reading the same transcript each see it in their own
language. Anything the platform is only passing on, like a compiler diagnostic or an error from the
underlying store, stays verbatim: it is the same text in every language, and translating half of it
would be harder to read than leaving it alone.

Older activities keep the text they were recorded with, so nothing already in your history changes or
disappears.
