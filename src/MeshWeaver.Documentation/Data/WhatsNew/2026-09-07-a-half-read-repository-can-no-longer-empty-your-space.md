---
Name: A half-read repository can no longer empty your Space
Category: Fix
Description: When GitHub returns only part of a large repository's file list — which it does silently, with a success code — a sync used to read every file it had not been shown as one you had deleted, and remove the matching pages. It now imports what it read and removes nothing.
Icon: ShieldCheckmark
Order: -20260907
---

# A half-read repository can no longer empty your Space

A Space that syncs with a repository stays a mirror of it: pages you add to the repository appear,
and pages you delete from the repository disappear. That second half rests on a question the sync
asks after every fetch — *which of my pages are no longer in the repository?* — and on the
assumption that it was shown the whole repository when it asked.

For a large enough repository, it was not.

## What was going wrong

GitHub caps how much of a file listing it will return in one answer. Past that cap it returns the
part it can, marks the answer as incomplete **inside** the response, and reports success. Nothing
about the reply looks like a failure: the sync received a valid list of files at a valid commit, and
carried on.

The sync then compared that partial list against the Space and concluded that every page whose file
had simply not been returned had been deleted from the repository — and deleted it. The larger the
repository, the more of the Space went with it, and the only record left was a tidy list of removals
that looked exactly like removals you had asked for.

## What happens now

The sync reads the "this answer is incomplete" marker, and when it sees it, **removes nothing at
all**. Everything it did receive is still imported, so the Space keeps moving forward; only the
"…and therefore the rest was deleted" conclusion is withheld, because it was never something the
sync had actually read.

It also says so, rather than leaving you to infer it from a quiet import:

> ⛔ Pruned nothing: the source listing came back incomplete (the repository tree was truncated), so
> a page missing from it is an unread file, not a deleted one. Everything that was read has been
> imported; nothing was removed.

The trade is deliberate. A page that really was retired stays in your Space for one more sync —
visible, and gone the next time the listing arrives whole. The alternative was deleting live pages
on the strength of an answer nobody had received, and leaving a log that could not tell the
difference.

## One thing to know

If a Space has already accumulated files the repository retired, this change does not clear them —
it stops the class of problem from being created. Re-syncing once the listing reads in full is what
removes them.
