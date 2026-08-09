---
Name: New /thread skill — manage conversation threads without the guesswork
Category: Feature
Description: The chat assistant now has a built-in /thread skill that knows the thread lifecycle — mark threads Done (one or in bulk) with a single patch each, no schema exploration.
Icon: Sparkle
Order: -20260730
---

# New /thread skill — manage conversation threads without the guesswork

The skill catalog now includes **/thread**, a built-in guide to managing your conversation threads.
Ask the assistant to close a thread, or to clean up your thread list ("mark everything done except
the last few"), and it now knows exactly what to do: a thread's lifecycle lives on its
`content.status`, marking one Done is a single idempotent patch, and bulk housekeeping is one
listing plus one patch per thread.

The skill also teaches the assistant what *not* to touch — the message bookkeeping that the
execution engine owns — and how to recognize a genuinely stuck thread and report it instead of
fighting it. The practical effect: thread housekeeping requests complete in seconds instead of
wandering through schema exploration.
