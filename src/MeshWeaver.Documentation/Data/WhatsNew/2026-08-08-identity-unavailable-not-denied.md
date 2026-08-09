---
Name: A storage blip no longer tells you that you have no account
Category: Fix
Description: A user record or permission lookup that cannot be read now says so, instead of redirecting a signed-in user to sign-up or answering "Access denied".
Icon: ShieldCheckmark
Order: -20260808
---

# A storage blip no longer tells you that you have no account

When the portal could not read your user record — a storage stall, a wedged query layer — it
used to conclude that the record was not there. The consequence was the worst possible one: a
fully signed-in user was redirected to the **sign-up form**, as if their account did not exist.
Signing in again could not help, because signing in was never the problem.

The same collapse hit permissions. If the grant lookup timed out, you were signed in with **no
roles at all** — and every page you opened answered "Access denied". Indistinguishable, from the
outside, from actually having lost your access.

Both were one mistake made twice: *"we could not find out"* and *"we found out, and the answer
is no"* were stored in the same value, so nothing downstream could tell them apart.

Now they are separate outcomes, decided where the timeout actually happens. A lookup that
reaches no verdict is reported as what it is — temporarily unavailable, with a `Retry-After` —
and the page says plainly that your sign-in is fine and that signing in again will not help.
A lookup that *does* reach a verdict is unchanged: a genuinely unknown user still goes to
onboarding, and a user with no grants still gets an empty role set.

API tokens got the same treatment earlier today; this completes it for browser sign-in and for
the role enrichment that API-token requests share.
