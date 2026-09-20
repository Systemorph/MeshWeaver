---
Name: One failing check no longer silently disarms the rest
Category: Fix
Description: A content repository's pull requests are held to about forty automated checks. They ran in one sequence, so the first failure stopped the rest — and a check that never ran looks exactly like a check that passed. For a while this morning, every pull request in five repositories was unguarded by six of them while one unrelated red was on screen. Each check now reports its own verdict.
Icon: ShieldCheckmark
Order: -20260920
---

# One failing check no longer silently disarms the rest

Every content repository — courses, demos, client spaces — runs the same validation pass over its
pull requests. About forty checks: are the module locks current, does every version match its
content, is each secret the build needs actually declared, does any configuration file define the
same key twice, do the pinned commits still exist.

They ran as one sequence. And a sequence stops at the first failure.

## Why that is worse than it sounds

A check that fails is red and someone fixes it. A check that never *ran* reports nothing at all —
and "nothing at all" is indistinguishable from "passed", both on screen and to the rule that decides
whether a pull request may merge. A branch protected by a check that silently did not run is not
protected.

So when one check went red part-way through the sequence, the sixteen behind it were skipped, and
the pull request could merge having been examined by neither the skipped checks nor anyone who
noticed. The only visible symptom was the one red, about an unrelated file.

This was not hypothetical. On the morning of 19 September a routine drift in one shared script put
all five content repositories into exactly this state within fourteen minutes, and six real checks —
including the one that catches a missing build secret, and the one that catches a module shipping a
version that describes content it no longer has — stopped guarding anything.

## What changed

Every check now reports its own verdict regardless of what failed before it. A failure still fails
the pull request, exactly as it did; what changes is that the other thirty-nine still say what they
found. A red is now a list of what is actually wrong, rather than the first thing that happened to
be wrong.

## And it cannot come back quietly

The defect was invisible on inspection — nothing in the configuration was incorrect, the *ordering*
was. So a new check was added whose only job is to refuse this shape: if any check in a protected
sequence could be skipped by an earlier failure, it names every one of them and fails. Run against
the configuration as it stood before this change, it names all thirty-nine.
