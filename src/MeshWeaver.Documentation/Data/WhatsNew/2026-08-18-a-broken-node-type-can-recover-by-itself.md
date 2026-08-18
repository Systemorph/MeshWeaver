---
Name: A broken NodeType can recover by itself
Category: Fix
Description: A NodeType whose very first build failed is now rebuilt automatically when the platform, its modules or its code change — instead of staying broken until someone presses Compile.
Icon: ArrowSync
Order: -20260818
---

# A broken NodeType can recover by itself

When a NodeType's build fails, the page it powers shows an error card instead of its content. That
is meant to be temporary: fix the code, or deploy a platform version that fixes it, and the type
rebuilds. For one particular kind of failure it was not temporary at all.

If a NodeType had *never* built successfully on that installation — a fresh import, a first
deployment, or a type whose failed state came in with the content — nothing would ever try again.
Every automatic rebuild the platform performs was looking for traces that only a successful build
leaves behind, and a failed build leaves none of them. So a new platform release, a module update
and even a corrected source file all went past it, and the only way out was for someone to open the
type and press Compile. Types stayed broken for days that way, including through the very release
that contained their fix.

A failed build now records what it was built against — the platform version, the installed modules
and the exact source files it used. When any of those change, the type gets one fresh attempt on
its own. A type that is simply broken is retried once and then left alone, with a clear line in the
log naming it, what it failed on and that only a manual Compile will move it — so a recoverable
failure recovers, and an unrecoverable one is visibly stuck rather than silently forgotten.
