---
Name: Every one of your roles is read at sign-in
Category: Fix
Description: Users holding more than ten access grants could silently lose the rest of them, and see "Access denied" on screens they should reach.
Icon: Sparkle
Order: -20260828
---

# Every one of your roles is read at sign-in

If you held more than ten access grants, only ten of them were read when you signed in. The rest
were dropped, so roles you genuinely had simply were not there — and screens those roles unlock
answered "Access denied" instead. Nothing failed and nothing was logged, because a shortened list
of grants looks exactly like a shorter list of grants.

All of your grants are now read, however many there are. The same applies to the lookup that finds
your account in the first place, so a signed-in user is no longer at risk of being sent back to the
sign-up form as though they had no account.
