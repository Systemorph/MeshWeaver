---
Name: An install that cannot read its update policy no longer updates itself
Category: Fix
Description: If an install's update policy went missing, it used to carry on auto-updating as though someone had chosen that. It now stops and waits to be told, which is the safer of the two guesses.
Icon: ShieldError
Order: -20260907
---

# An install that cannot read its update policy no longer updates itself

An install records whether it should update itself automatically. If that setting went missing — and
it could, under the install's own routine bookkeeping — the install read it back as *"yes, update
automatically"*. The most permissive answer, arrived at by losing information rather than by anyone
choosing it. That is how one portal rolled itself onto a version line that had been withdrawn.

A missing setting now reads as **do not update**. The install waits to be told instead of guessing,
and an explicitly chosen setting — including *never update* — is always recorded, so "someone chose
this" and "this went missing" are no longer the same thing.
