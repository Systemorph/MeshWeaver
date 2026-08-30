---
Name: A permission check that reaches no answer no longer lets the request through
Category: Fix
Description: When the access check finished without deciding anything, the request used to be delivered anyway; it is now refused and reported as temporarily unavailable.
Icon: ShieldError
Order: -20260830
---

# A permission check that reaches no answer no longer lets the request through

Every request that touches protected content is gated by an access check. That check reads your grants,
your groups, the policies on the space and each of its parents, and combines them into one answer. It
could finish in three ways: with an answer, with an error — or by simply stopping, having produced
nothing at all, because one of those reads never returned a value.

The third way had no handling. A check that produced *nothing* was not treated as a refusal: nothing
had objected, so the request was passed on and answered normally. In other words, an access check that
established nothing could let a request through instead of stopping it. Nothing in the logs said so.

Such a check is now reported for what it is — **no answer was reached** — which refuses the request and
tells the caller it is temporarily unavailable and worth retrying. It is deliberately *not* reported as
"access denied": no decision was made about your rights, so saying otherwise would send you off to ask
for permissions you may already have.

Nothing changes for a check that does reach an answer. A grant still grants and a denial still reads as
a denial, in exactly the words it used before.

## Also recorded, not changed: why the access check waits

The same investigation looked at the related complaint that a first read of an idle page can sit
waiting, then succeed on a retry. The tempting fix — let the check answer from whatever it has so far,
rather than waiting for every read — turns out to be unsafe: three of the four reads can *remove*
access as well as grant it (a denial aimed at one of your groups, a space policy that caps what an
editor may do), so answering early from an incomplete picture would hand out access the full picture
revokes. The reasoning, and what a correct fix would have to look like instead, is now written down in
*Architecture → Access Control → The convergence contract*, together with the checks that keep the
rule honest.
