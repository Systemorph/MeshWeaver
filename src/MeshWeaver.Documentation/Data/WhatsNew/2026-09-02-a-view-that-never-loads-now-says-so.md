---
Name: A view that never loads now says so instead of waiting forever
Category: Fix
Description: When the same connection kept losing the fresh copy a stuck page asked for, the page waited in silence for good; it now reports the failure so it can reconnect.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# A view that never loads now says so instead of waiting forever

Live areas on a page are kept current by a stream of small updates. If one goes missing on the way,
the page notices and asks the server for a complete, fresh copy. A previous fix made that recovery
survive the fresh copy *also* going missing: the page asks again the next time something changes,
which costs one more round trip instead of the rest of the session.

What was still missing is what happens when the same connection keeps losing **every** fresh copy.
The page asked, the server answered, the answer never arrived — over and over, forever, without a
word to the part of the app that draws the area. On screen that was an area stuck on "loading" on a
page whose menus, banner and breadcrumb were fine, with nothing to click and no error to report.
It happened during a live presentation on 1 September: a slide deck whose presentation view never
appeared, while the same page's plain content loaded instantly. Reloading the page could clear it;
restarting the server could not, because nothing was wrong on the server.

Now the page stops asking after a few unanswered attempts and **reports the failure** instead. That
one change is what makes recovery possible at all: the failed view is dropped rather than kept, so
the next thing that needs it opens a fresh connection and loads normally — the same thing a reload
used to do, without the reload.

Two things this deliberately is not. It is not a timer: the page only ever asks again when a real
update arrives proving it still has nothing to show, so a quiet page stays quiet. And it is not a
retry budget — only attempts the server *confirmed it had handled* count, each one costing a full
round trip plus a fresh piece of evidence, so a handful of them is proof the connection is dropping
this view's data rather than impatience. An attempt the server turns down because it is briefly
busy — during a rolling update, say — is waited out as before and never counts against the view.

For anyone reading server logs, the give-up is recorded at warning level naming the view and the
server it was asking, right after the `Resync has not converged` warnings that precede it.
