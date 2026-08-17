---
Name: A new build is offered, not forced
Category: Fix
Description: Publishing a new build of a type no longer restarts every open instance of it — the page tells you a newer build is available and you choose when to pick it up.
Icon: ArrowSync
Order: -20260817
---

# A new build is offered, not forced

Until now, compiling a new build of a node type **silently restarted every live instance of that
type**. If someone published while you had one of those pages open — mid-edit, mid-scroll, mid-form
— your page's hub was torn down underneath you and rebuilt. Nobody asked, and nothing said it had
happened. Publication frequency was restart frequency: on a busy type, a handful of publishes in a
minute meant a handful of restarts for everyone reading it.

That automatic restart is gone. When a type publishes a build newer than the one your page is
running, the page now shows a short notice above its normal content:

> **A newer build of this type is available.** This page is still running the previously compiled
> one. — *Recycle*

Everything on the page keeps working exactly as before. The notice sits **above** your content, it
does not replace it, and clicking **Recycle** takes you to the ordinary confirmation before
anything is torn down. Nothing on the page is lost because a build landed somewhere else.

**The consequence, stated plainly:** if nobody clicks, that page keeps running the older build
indefinitely. That is deliberate and safe — the older build is one that compiled and worked — but it
does mean "published" no longer implies "every open page is already running it". If you want a page
on the newest build, recycle it; if you want it left alone, leave it alone. That decision is now
yours rather than the platform's.

Two states that used to look similar are now clearly different. A type that **failed to compile**
still replaces the page, because there is nothing good left to show. A type that merely has a
**newer build** only adds a line above content that is working fine. Broken and merely-stale no
longer wear the same face.
