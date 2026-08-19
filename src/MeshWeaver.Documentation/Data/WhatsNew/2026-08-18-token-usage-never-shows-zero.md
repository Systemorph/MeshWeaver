---
Name: Token usage no longer flashes zero
Category: Fix
Description: A thread's token chip now shows the real counts the moment they exist, instead of briefly reading 0 tokens and $0.
Icon: Sparkle
Order: -20260818
---

# Token usage no longer flashes zero

After a chat round finished, the token chip on a thread could briefly show `↑0 ↓0 · $0` before
snapping to the real numbers. That was not a display delay — the usage record was genuinely written
twice: once empty, and again a moment later with the round's counts.

The empty write is gone. A round's token counts are now saved in a single step, so the chip shows
the true figures as soon as the record exists. This also closes a rarer and worse case: if that
second write was lost — under heavy load it could be abandoned after 15 seconds, silently — the
zeros became the permanent record of a round that really had consumed tokens. There is no longer a
second write to lose on a thread's first round with a model, and when one is abandoned it now says
so in the log instead of failing quietly.
