---
Name: A merge helper that reported a merge nobody had checked
Category: Fix
Description: The helper that finishes a merge whose only conflicts are generated files could report "the merge can be committed" in three situations where it had established nothing of the kind — when the tool it asks was unable to answer, when the conflicting file was one it does not generate, and when a file it was about to record was not part of the merge at all. All three were silent, and all three ended the same way — a merge that looked finished, and a build that failed later somewhere else.
Icon: Bug
Order: -20260919
---

# A merge helper that reported a merge nobody had checked

Every content repository carries small generated files that record exactly what each module
contains. Two branches that both changed a module both rewrite that record, so merging them
conflicts on it — and there is no sensible way to merge two such records by hand, because each one
describes its own branch's files and neither describes the merged result. The only correct answer
is to throw both away and regenerate from the merged tree.

A helper does that automatically: it checks that the only conflicts are those generated records,
regenerates them, records the result, confirms nothing is left unresolved, and says the merge can be
committed. Three of those steps could report success without having checked anything.

## When the tool cannot answer, that is not a clean answer

The final step asks the version-control tool whether any conflict remains. The tool answers with a
list — and, when it cannot run at all, with nothing. The helper read those two answers as the same
thing: an empty list. So a question it could not ask was recorded as "no conflicts remain", and the
run printed that the merge was ready.

That is a check incapable of failing, over the single question the helper exists to answer. It now
distinguishes the two: an empty list still means resolved, and an unanswerable question stops the
run and says so in those words. The same confusion was in two neighbouring checks, and both were
fixed with it — a step that cannot read its input now refuses instead of passing.

## A file with the right name is not necessarily a file we generate

The helper recognised the generated records by their file name. But it generates exactly one such
record per module, at the top of that module's folder — and a file with the same name somewhere
else, in a tooling folder or nested deeper inside a module, is an ordinary file that a person has to
merge.

Those were being treated as generated. Regeneration left them untouched, because they are not
modules; the helper then recorded them anyway, conflict markers and all, and reported the merge
ready to commit. It now recognises only the records it actually writes, and refuses everything else
as the real conflict it is.

## A file that is not in the merge must not be recorded as though it were

Before regenerating, the helper refuses if the working folder holds edits that are not part of the
merge — otherwise those edits get baked into the record and travel into the merge commit where
nobody is looking for them.

It asked the version-control tool for those, and the tool deliberately hides files the repository is
configured to ignore: build outputs, test results, scratch files. The part that builds the record
does not ask the tool at all — it reads the folder directly, and hashes those same ignored files.
So an ignored test-result file sitting in a module went into the record without appearing in the
check, and the build later failed on a record describing a file that exists in no commit.

Both halves now read the same list of files, produced by the same code rather than by two
descriptions of it that could drift apart. Ignored files that the record will genuinely contain now
stop the helper; ignored files it never reads — the great majority, such as build folders — still do
not, so the check stays usable.

## Proving each one

Each of the three now has a case in the helper's own self-test, and each case was confirmed to fail
when the corresponding fix is removed: the self-test is run by every content repository's checks, so
a future change that reintroduces any of them is caught where it happens rather than in a merge
commit weeks later.
