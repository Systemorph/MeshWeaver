---
Name: A click that could not run says why it could not run
Category: Fix
Description: When the portal has to refuse a click, blur or dialog dismissal, it now says which of three things happened to the view it came from — instead of one sentence that listed all three and left the reader to guess.
Icon: Bug
Order: -20260915
---

# A click that could not run says why it could not run

Very occasionally the portal has to **refuse** something you did — a click, a tab-out of a field, a
dialog you closed. The view it came from had already gone, so the button's action has nowhere left
to run. That is unchanged, and it is deliberate: you are told the action did not happen rather than
being left to wonder.

What changed is the record the refusal leaves behind.

Until now it said the same thing every time:

> the target stream is gone (disposed circuit, released read stream, or never-created sync hub)

Three different situations, one sentence. Whoever went looking — a person reporting it, or the
automatic incident report that gets filed — had no way to tell them apart, so unrelated events were
filed together and the wrong one was investigated twice.

**The refusal now names which one it was:**

- **The page you were on had already closed it.** You navigated away, or the view released itself,
  and the click was still in the air. Nothing is wrong; this is the case the portal is designed to
  refuse.
- **The portal ended the connection while you were still on the page.** The view was live, and the
  platform — not your browser — dropped it. This is the one worth chasing, and now it says so.
- **The part of the portal you were talking to had restarted.** Your page was still holding a
  connection to something that no longer exists.

There is also a fourth line, and it is the honest one: if the portal no longer holds enough history
to tell the last two apart, it says exactly that — and shows the numbers it is working from —
rather than guessing.

Nothing waits longer, nothing is retried, and no refused action was turned into a silently accepted
one. A refused action is still refused, and you are still told. The only thing that changed is that
the record now points at the right thing.
