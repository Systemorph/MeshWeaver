---
Name: A hold that nothing is checking any more says so
Category: Fix
Description: With automatic updates switched off, the Updates tab kept showing the last refusal it had recorded — sometimes days old, sometimes naming problems that had since been fixed — worded as if it were the reason the install was standing still right now. The record is still shown, because it is the one thing worth having before switching updates back on; it is now labelled as a record.
Icon: History
Order: -20260909
---

The Updates tab has two pieces of information that look like one: **when this install last checked**,
and **what it decided the last time it actually evaluated a build**. While updates are switched on
those move together and nobody can tell them apart. Switch updates off and they come apart
completely — the check keeps running and keeps noting *"updates are disabled on this install"*,
while the last real verdict sits underneath, unchanged, being read as current.

An install seen this week had recorded a refusal at **22:27 on 7 September** and last checked at
**10:41 on 9 September** — thirty-six hours later. On screen, the refusal read as the reason the
install was not moving. It was not. Two of the problems it named had been fixed **two hours after it
was written**, and nothing had looked at it since.

## What changed

Nothing about the record. It is still kept, and still quoted in full — it is the one useful thing to
read *before* switching updates back on, and throwing it away to avoid showing something stale
would trade a misleading answer for no answer at all.

What changed is that it now says what it is:

> 🗄️ **This is a record, not a current verdict.** Automatic updates are switched off on this
> install, so nothing has re-checked `3.0.0-ci.8057` since 2026-09-07 22:27 — what follows is the
> last evaluation that ran, and it may name problems that have since been fixed. Switch updates on
> to have it re-checked.

The *"(held «date»)"* line goes with it, because that phrasing describes something still happening.

The **About** page follows the same rule: it no longer reports *"update held"* on the strength of a
note nothing can refresh. A build that a verification actually **refused** still reads as held —
that is a recorded fact about the build, not a note about the checker, and the difference is now the
one that decides.

## Why it is worth a release note

Because it wasted a day. An issue was opened against this platform arguing that three retired
packages were holding every update on an install *forever*, quoting the frozen text as evidence. The
mechanism it described had been removed from the product two hours after that text was written. The
report was careful, the reasoning was sound, and the screen it was reading from gave it no way to
know.
