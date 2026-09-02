---
Name: Being refused now says so, instead of looking broken
Category: Fix
Description: An assistant that asked to change something it lacked access to got an internal error with a stack trace, and sometimes got the change anyway. Every tool surface now answers refusals in plain words — and the checks behind them, which had been granting everything, actually run.
Icon: ShieldCheck
Order: -20260902
---

# Being refused now says so, instead of looking broken

Ask an assistant to tidy up a space you can only read, and it would come back with something like
*"the recycle tool threw an unhandled exception"*, followed by a stack trace. The refusal itself was
right — you may not change that space. What was wrong was that nothing said so. From the outside,
"you are not allowed to do this" and "this tool is broken" looked identical, so the assistant would
often try again, or start guessing at repairs for a fault that never existed.

Refusals now come back as answers, in the words you would want: *"Recycle requires Update permission
on the target node — ask someone with write access to the node (or a platform admin) to do it."*

## The part that was worse

Chasing the crash turned up why the friendly message had never appeared: the permission check meant
to produce it was, on these surfaces, always answering *yes*.

Every assistant, script and API call runs its work on a per-session workspace, and that workspace
had never been handed the rules about who may do what. Asked whether you had permission, it said yes
to everything. The mesh itself still refused — the real gate sits with whoever owns the data, and it
held — but the polite pre-check in front of it had no opinion at all, so nothing ever reached the
readable explanation.

Two things slipped through that gap, and both are now closed:

- **Restarting a node you could only read.** Restarting is "note the request, then bounce it". On
  most nodes there is nothing to note, so nothing was checked, and the bounce happened anyway.
- **Exporting a space.** An export filters out what you are not allowed to take with you. That
  filter ran on the same opinionless workspace, so it kept everything.

If you have been using an assistant against a space you only have read access to, it now behaves the
way you would expect: it tells you it cannot, and does not quietly do it.

## Told apart on purpose

One distinction is kept carefully. *"You do not have permission"* is final and worth acting on.
*"I could not check right now"* is temporary and worth retrying — and telling you the first when the
truth is the second would send you asking for access you already have. The two keep separate
wording, and the difference is decided from what the mesh actually reported, never guessed from how
a message reads.

The engineering detail is in [A Denial Is an Answer](/Doc/Architecture/DenialIsAnAnswer).
