---
Name: A page waiting on a compile now shows live progress
Category: Feature
Description: Opening a node while its type is still compiling — for example right after an update recompiles every dynamic type — now shows a live progress page with the compile log and an "N of M types compiled" queue, then returns to your page automatically. Previously the page sat blank until a timeout.
Icon: Sparkle
Order: -20260811
---

# A page waiting on a compile now shows live progress

After a platform update, every dynamically-defined node type is recompiled — and opening a
node whose type had not yet finished used to mean staring at a blank page until a one-minute
timeout gave up with "Area unavailable". Nothing told you a compile was running, how far along
it was, or that the page would come back on its own.

Now, when a page's type has been compiling for more than a few seconds, the page switches to a
live progress view instead of waiting silently:

- the type's compile **status and streaming compile log**, updating line by line;
- when more types are queued behind it, the **whole queue**: an "N of M types compiled"
  progress bar, the type currently compiling, and how many are still waiting;
- and when the compile finishes, the view **returns to your page automatically** — no reload
  needed.

Short compiles stay invisible: a type that finishes within the grace period opens your page
directly, exactly as before. Requests sent to a node mid-compile now also fail fast with a
clear "type is compiling" answer instead of hanging until their own timeout.
