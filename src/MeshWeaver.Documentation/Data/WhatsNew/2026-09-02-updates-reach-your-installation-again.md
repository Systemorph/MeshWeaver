---
Name: Updates reach your installation again
Category: Fix
Description: For six hours, every merge to the platform's main branch landed without starting the checks that release depends on, so nothing new was ever published and self-updating installations quietly stayed on an older build. Merges now start those checks again.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# Updates reach your installation again

An installation that keeps itself up to date does it by watching for a new published build. On
2026-09-01, for about six hours, there were none — not because anything failed, but because nothing
was ever offered.

Work was landing normally the whole time. Changes were reviewed, tested and merged; the branch moved
forward four times. What did not happen was the step *after* the merge: the platform's build-and-test
run, which is the evidence the release process waits for before it publishes anything. The release
process asked for that evidence on every check, did not find it, and did the correct thing — it
waited. It waited on evidence that was never going to arrive.

The cause was a credential, and it is worth stating plainly because the symptom points somewhere
else entirely. A lane had recently been added to merge a change automatically once it went green, so
that finished work would stop sitting around waiting for someone to press the button. That lane
signed its merges with the automation's own built-in identity — and merges made with that identity
deliberately do not start further automation. It is a sensible rule in general: it stops automation
from triggering itself in a loop. Here it meant the merge landed and the checks that should have
followed it simply never began.

Nothing was red. There was no failed run to find, no error, no warning — only an absence, which is
the hardest thing to notice. From the outside it looked exactly like a build that had not started
*yet*.

**The merge lane now signs with an identity whose merges behave like anyone else's**, so the checks
run, the evidence appears, and publication proceeds. Three things were changed together so this
cannot come back quietly:

- If that identity is ever missing or under-privileged, the lane now **fails loudly and names
  exactly what to provision** — rather than falling back to the credential that caused this.
- A build-time check refuses any future attempt to merge with the built-in identity, with the
  explanation attached.
- The release process's own account of what it needs is written down, including how to tell "the
  checks have not run yet" apart from "the checks will never run" — the distinction that made six
  hours of silence look normal.

If your installation looked stuck on an older version yesterday, it was not stuck: there was
genuinely nothing newer to take. It will pick up the current build on its next check, with no action
from you.
