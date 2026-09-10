---
Name: A re-import of unchanged content can no longer go quiet
Category: Fix
Description: Saving something that had not actually changed could, in one narrow case, produce no answer at all — not a success, not a refusal, nothing. Whatever was waiting on it then sat there until its own patience ran out, with nothing anywhere saying why.
Icon: Wrench
Order: -20260910
---

# A re-import of unchanged content can no longer go quiet

Re-installing an app, re-syncing a repository, re-running an import — these mostly write content
that is already there, byte for byte. The platform notices that and skips the write, which is the
right thing to do: an identical save that goes through anyway bumps a version, adds a history entry,
and can look enough like a real edit to make a two-way sync start defending a copy nobody touched.

Before skipping, it checks one thing: **could the person or process asking have written this
anyway?** Skipping a write for someone who was not allowed to make it would be a success without a
save — the worst of both answers — so the check exists to stop exactly that.

## What was wrong

That check could come back with **no answer at all**.

Not "yes", not "no", not an error — simply nothing, which is a real and already-known outcome for a
permission lookup. And nothing was precisely what happened next: the save was not skipped, the save
was not attempted, no refusal was sent, and nothing was written to the log. The request that started
it all had already been acknowledged as *received*, so from the outside everything looked normal
until the caller's own patience ran out — a minute later, ten minutes later, depending on who was
waiting.

For a person, that is a save that appears to hang. For an automated install re-running to confirm it
is repeatable, it is a step that stalls and then reports a failure naming a node rather than the
thing that actually went wrong.

## What changes

**A non-answer is now treated as what it is: not an answer.** When the permission check produces no
verdict, the platform stops trying to decide locally and simply performs the ordinary save, letting
the node's owner rule on it the way it rules on every other write. That is already what happens when
the check fails outright, so silence and failure now behave the same — which is the point, because
they mean the same thing.

Every other place the same request can hand its reply to background work was given the same
treatment, so this class of silence is now closed by construction rather than by remembering to
handle it: each one produces an outcome on every path, and an outcome always gets answered. Where the
outcome is genuinely unknown, the answer says so, and says it is unknown — never "it did not happen",
because a missing reply is not evidence that nothing was written.

## What you will notice

Almost always, nothing — the check answers normally and this never comes up. When it does not, a
re-import or a save now finishes with a real result instead of going quiet, and if the outcome cannot
be established you get a message that tells you so and tells you to check the node's state before
retrying.
