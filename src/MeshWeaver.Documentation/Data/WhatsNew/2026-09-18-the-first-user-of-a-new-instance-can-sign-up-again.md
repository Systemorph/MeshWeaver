---
Name: The first person on a brand-new instance can sign up again
Category: Fix
Description: On a freshly created deployment the very first sign-in claimed the person's own username before they reached the form, so onboarding refused it and the instance never got an administrator.
Icon: PersonAdd
Order: -20260918
---

# The first person on a brand-new instance can sign up again

Signing in for the first time on a **new** deployment ended at the onboarding form with a refusal of the one name that should always have been available:

> Username 'name' is already taken. Please choose a different one.

No other name was a real answer — it is your name — and because nobody could complete onboarding, the instance never got its first administrator. Re-installing did not help: the name was claimed again three seconds after every sign-in.

**What was happening.** An instance with no users yet has no user index, so sign-in falls back to the local part of the email address as the person's workspace name. The platform's per-logon setup steps then found no profile for that name, read that as *nothing has been set up for this user yet*, and seeded their default apps — and creating anything inside an empty workspace brings the workspace itself into being. By the time the onboarding form asked which username you wanted, a workspace already stood at exactly that name, and the form's check — the one that stops a sign-up overwriting an existing person's workspace — correctly refused it.

**The fix:** the per-logon setup steps no longer run at all for someone who has no profile yet. Onboarding creates the workspace, as it always did; until it has, there is nobody there to set up. Someone whose profile simply could not be read in time is also left alone now, and their setup steps run at the next sign-in instead.
