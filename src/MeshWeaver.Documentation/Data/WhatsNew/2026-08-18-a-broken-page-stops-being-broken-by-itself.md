---
Name: A broken page stops being broken by itself
Category: Fix
Description: The "this page can't be displayed" card now keeps re-checking the type it is waiting for and clears itself the moment the build settles, instead of staying on screen after the cause is gone.
Icon: ArrowSync
Order: -20260818
---

# A broken page stops being broken by itself

When a page's type is still compiling — right after a platform update, or while a
bulk rebuild is queued — the page shows a card that says so, and promises to come
back on its own once the build settles.

It did not always keep that promise. If the page never heard that the build had
finished, the card stayed. On 2026-08-17 every course cover on the public site kept
serving that card for **an hour and twenty-four minutes after the type had compiled
successfully** — nothing was retrying, and the only way out was for an operator to
recycle twelve pages by hand.

The reason was narrow and worth naming: the card was waiting to be *told* the build
had settled, over the very channel whose silence had produced the card in the first
place. One broken channel, and both the fault and its cure went with it.

Now the page **checks for itself**. A page showing the card re-reads its type's build
from the store — after about a minute, then less and less often — and recycles onto
the real page as soon as there is something to bind. No notification has to arrive,
no operator has to intervene, and nothing has to be running for it to work.

Two things deliberately did **not** change:

- **A genuinely broken type still shows its card.** The goal is a card that clears
  when its cause clears, never one that hides a real problem. If the type does not
  build, the page keeps saying so — and says it in the log too, so it is visible
  before a user reports it.
- **Re-checking stays cheap.** It is a widening interval, not a poll: a page that
  cannot recover settles at one check every ten minutes, so a struggling mesh is
  never asked to carry a retry storm on top of whatever it is already dealing with.

If you would rather not wait, the **Recycle** menu still refreshes a page
immediately — it is now a shortcut rather than the only way back.
