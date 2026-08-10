---
Name: The editor no longer forgets which completions you prefer
Category: Fix
Description: The editor's memory of the suggestion you usually pick could be wiped down to your most recent choice when it could not be read back; it is now left untouched until it can be.
Icon: Sparkle
Order: -20260810
---

# The editor no longer forgets which completions you prefer

The code editor learns from you. Each time you accept a suggestion it remembers the choice, so the
next time the same list comes up the item you actually use is already highlighted — you press Enter
instead of scrolling. That memory builds up over months of editing and is worth quite a lot by the
time you notice it.

It could be thrown away. Your history is stored alongside your settings, and the editor reads it
once when you start working. If that read did not come back quickly enough — a busy moment, storage
catching its breath — the editor concluded you had no history at all rather than that it had not
managed to look. From then on it worked from an empty slate, and the moment you accepted your next
suggestion it saved that single choice back in place of everything you had built up. Nothing failed
and nothing was reported; the highlighting was simply worse than it used to be, and stayed that way.

A read that does not come back is now treated as what it is — no answer, rather than an answer of
"nothing" — and the editor will not save over a history it has not managed to read. It quietly tries
again the next time you ask for completions, and until it succeeds it keeps your stored history
untouched. Suggestions you accept in the meantime still get remembered for the rest of your session;
they are written out once the earlier history is safely back in hand.
