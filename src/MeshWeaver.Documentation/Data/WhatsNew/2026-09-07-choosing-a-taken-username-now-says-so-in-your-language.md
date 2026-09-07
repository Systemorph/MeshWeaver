---
Name: Choosing a taken username now says so in your language
Category: Fix
Description: The messages the sign-up form shows when it cannot accept a profile — the username is taken, the email already belongs to an account, the portal is invitation-only — were English for everyone. They are now translated.
Icon: Globe
Order: -20260907
---

# Choosing a taken username now says so in your language

The first screen a new person sees is the sign-up form, and it is the one screen where the language
picker sits at the top — chosen before anything else precisely so the rest of the form, and the
screens after it, arrive in the language they picked.

Everything on that form followed the choice except the messages that matter most: the ones that come
back when the form **cannot** be accepted.

- *Username '…' is already taken. Please choose a different one.*
- *This email is already assigned to user '…'. Please sign in with that account.*
- *This portal is invitation-only and your email has not been invited.*
- *Registration is closed.*
- *Failed to save profile: …*

All five were written into the page as English sentences, so a German speaker who had just told the
form they wanted German was answered in English — at the one moment they most needed to understand
what to do next.

## What changes

**The five messages are now catalog entries**, translated into every language the portal ships, and
resolved against the language the person picked on the form rather than against whatever language
the portal itself happens to be running in.

That distinction is the reason the fix is not simply "wrap them in a lookup". The checks behind
these messages run under the platform's own identity — they have to, because the person filling in
the form does not have an account yet, which is the whole point of the page. Resolving the language
inside that scope would answer in the platform's language, not the visitor's. The visitor's choice
is now read **before** those checks begin and carried into the message.

## What it does not change

**Nothing about who is accepted.** The same names are refused, for the same reasons, at the same
point. A username that is already in use stays refused — that check is what stands between a new
sign-up and overwriting an existing person's profile.
