---
Name: A broken import stops retrying itself
Category: Fix
Description: When content could not be imported because it breaks a rule, the platform re-imported the whole thing again on every trigger — the same failure, at full cost, forever. It now records the verdict and waits for the content to change.
Icon: ArrowRepeatAllOff
Order: -20260903
---

# A broken import stops retrying itself

Content that arrives from a repository is imported once per version. The platform fingerprints what
it received, and once that exact content has been imported it is skipped — that is what stops every
restart and every push re-importing the whole library.

That skip only ever recognised **success**. If the import had failed, the next trigger did the entire
thing again: every file, every write, and a fresh compile of everything compilable in it. And when
the failure was caused by the content itself — a page in a place its own rules do not allow, a type
the platform does not know — nothing about running it again could change the outcome. The same
content produced the same failure, and the work was spent for nothing.

On one portal that meant **nineteen complete re-imports in three hours**, each one failing on the
same 425 items and recompiling the same set, on an installation already running at full capacity.
Every green build of the source repository started another round.

**A failure caused by the content is now recorded as the answer for that version of the content.**
The next trigger sees it, skips, and says why. Fix the content — or ask for a forced re-import — and
it runs again immediately, because changing the content changes the fingerprint.

The distinction matters more than the saving, so it is drawn narrowly:

- **Only failures the content itself causes are final.** If anything transient was involved — the
  database briefly unreachable, a component restarting mid-import — the import is retried exactly as
  before. Re-running can genuinely help there, and refusing to would be far worse: it would leave
  content out of your installation with nothing re-examining it.
- **A partly-successful import is unchanged.** Everything importable still lands; only the items that
  cannot be imported are reported, and they are still named individually.
