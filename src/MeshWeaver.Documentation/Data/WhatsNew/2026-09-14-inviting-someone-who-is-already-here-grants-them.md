---
Name: Inviting someone who is already here grants them
Category: Fix
Description: Inviting a person by email to a Space or a group now grants an existing account straight away on every deployment — it used to send an invitation to someone who already had an account, and the pin on their dashboard could not land.
Icon: PersonAdd
Order: -20260914
---

# Inviting someone who is already here grants them

Inviting a person by email — to a Space, to a group, or through Access Control's grant-by-email —
has two cases: the address already has an account, and it is granted access now; or it does not,
and an invitation goes out with the grant scheduled for the moment they sign up.

On a deployed portal the first case was being decided wrong. The check that asks "is there an
account with this email" ran with the **inviter's** rights, and the directory it reads is one an
ordinary Space or group admin has no rights on — so the answer was "no account" for people who
were plainly already here. They received an invitation email instead of the access, and the
access only landed when the scheduled grant noticed a sign-up that was never going to happen.

**The lookup now runs as the platform**, as the portal's own sign-in directory already does. It
discloses nothing new: the inviter sees the same two outcomes as before, and the grant itself is
still written as the inviter and still refused where they hold no rights.

The same change fixes the pin: inviting to a Space also pins it on the invitee's dashboard, which
is the invitee's own node — the immediate path was writing it as the inviter, who has no rights
there, so the invite could fail after the grant had landed. It now pins exactly as the scheduled
path always did.
