---
Name: A stuck view now recovers instead of waiting forever
Category: Fix
Description: A page area that stopped updating because one update was lost in transit could stay blank for good; it now asks again until it is answered, and says so out loud when it cannot be.
Icon: ArrowSync
Order: -20260830
---

# A stuck view now recovers instead of waiting forever

Live views on a page are kept up to date by a stream of small updates rather than by re-sending the
whole picture each time. If one of those updates goes missing on the way, the page notices — it can
see that an update refers to a change it never received — and asks the server for a complete, fresh
copy. That part always worked.

What did not work is what happened when **the request for the fresh copy, or the copy itself, also
went missing**. It travels the same route that just proved it can lose things, and the page had no
way to notice: it waited for an answer that was never coming, quietly ignored every later update,
and stayed exactly as it was. On screen that looked like an area that never finished loading — a
blank panel, or an unrendered placeholder, on a page whose menus, banner and breadcrumb were all
perfectly fine. No error, no spinner that eventually gave up, nothing to click. Only a reload
helped, and after a reload it could happen again.

Three things now close that:

- **The request is tracked.** The page waits for the server to confirm it received the request. Once
  the round trip is over, the next update that arrives while the page still has nothing to show
  makes it ask again — so a lost request or a lost copy costs one more round trip instead of the
  rest of the session. Nothing polls and nothing retries on a timer; only real activity triggers a
  new attempt, so a quiet page stays quiet.
- **A refusal is reported.** If the server can no longer serve that view at all, the page now shows
  that failure instead of holding an empty placeholder for a copy that will never arrive.
- **A fresh copy is always accepted.** When the server had to rebuild its side of the connection to
  answer, its fresh copy could look "older" than what the page was holding and was discarded — even
  though the page had already thrown its own copy away and had nothing left to lose. A page with
  nothing to show now takes whatever complete copy it is given.

For anyone reading server logs: a single recovery is normal and stays quiet. A view that asks for a
fresh copy **again** — because the first attempt was never answered — is now recorded as a warning
naming the view and the server it is asking, so the rare case that genuinely does not recover is
visible instead of silent.
