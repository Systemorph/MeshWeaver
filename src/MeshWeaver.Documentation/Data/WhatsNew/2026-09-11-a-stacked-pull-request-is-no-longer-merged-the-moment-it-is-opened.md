---
Name: A stacked pull request is no longer merged the moment it is opened
Category: Fix
Description: A pull request opened onto another pull request's branch was auto-merged within a minute of being opened, before any of its own checks had started. Auto-merge is now only offered where the checks it waits for actually exist, and a pull request that does not qualify is told so on the pull request itself.
Icon: ShieldCheckmark
Order: -20260911
---

# A stacked pull request is no longer merged the moment it is opened

Every pull request that is not a draft is armed to merge itself as soon as its checks pass. That is
deliberate: it removes the wait between "everything is green" and "somebody pressed the button", and
it weakens nothing, because the merge still waits for exactly the checks the target branch requires.

The catch was in that last clause. What auto-merge waits for is whatever the **target** branch
requires — and a project protects its main branch, not every working branch on it. So a pull request
opened onto *another pull request's branch*, the ordinary way a larger change is split into
reviewable pieces, was pointed at a target that required nothing at all. "Merge when the checks
pass" then meant "merge now": there were no checks to wait for, and the condition was satisfied the
first time it was looked at.

It behaved exactly like that. One such pull request was merged 61 seconds after it was opened,
before a single one of its own checks had started to run. Nothing failed and nothing was
circumvented — the stack simply collapsed into its base while the author was still writing the
description.

Auto-merge is now offered only where the checks it waits for actually exist. A pull request aimed
anywhere else is left for a person to merge, which was always the intent, and it is **told so**: a
short comment on the pull request naming its target branch and why it was skipped. That matters as
much as the fix. An unarmed pull request and a broken automation look identical from outside — both
are a page where nothing happens — so the one case that is deliberate now says so out loud instead
of leaving the author waiting for something that was never coming.

Retargeting the pull request at the main branch arms it normally, with no further action.
