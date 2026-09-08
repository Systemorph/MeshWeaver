---
Name: A documentation edit no longer rebuilds everything
Category: Fix
Description: Changing one file at the top of a repository used to rebuild every module in it and run every module's tests — 105 build jobs for a diff that touched no code. The check that decides what a change reaches could not see files that live at the root, and answered a disagreement between two repositories by rebuilding everything, everywhere.
Icon: TargetArrow
Order: -20260908
---

# A documentation edit no longer rebuilds everything

Every content repository has a check that reads a pull request's changed files and works out which
of its modules that change can possibly reach. Only those get rebuilt, and only those get their
tests run. Everything it cannot classify gets rebuilt — deliberately, because guessing *narrower*
than the truth means shipping something that was never recompiled.

Two things it could have classified, it did not.

## A file at the top of a repository has no folder

The check recognises a harmless change by the **folder** it is in — release notes, documentation,
the mobile app, the end-to-end tests. A file that sits at the *top* of the repository is in no
folder at all, so it matched none of those rules and fell straight into "rebuild everything".

The file that matters here is the one every repository keeps at its root for the assistants working
in it. Editing it alone cost, measured on one real pull request: **105 build jobs, about 345 minutes
of machine time, 54 minutes of waiting — for a one-file documentation change.**

The same run now costs **31 jobs and about 131 minutes.** No module is rebuilt that the change
cannot reach, and no module's tests run at all.

The fix is a **named list**, not a pattern. A root file the list does not name is still unknown and
still rebuilds everything — which is what keeps the files that genuinely change how a repository
builds, and every repository's own gate settings, on the loud path where they belong. "Any markdown
file" would have made that promise on behalf of files nobody had read.

## Two copies of one list disagreed, and the answer was "rebuild everything"

The list of harmless folders is written twice: once in the platform, once in each content
repository. When the two differed at all, the platform stopped narrowing entirely and rebuilt
everything, on every pull request, indefinitely — and the only trace was one line inside the log of
the very job it had switched off.

Three repositories were in that state. In all three the disagreement was about a folder **none of
them has**, so it could never have applied to a single changed file. They had simply been rebuilding
everything, for months, over nothing.

A disagreement now costs only the folder it is about: that one folder is rebuilt, out loud, and
every other folder keeps narrowing. Whichever side calls it harmless, the other side's belief wins
— so the change can only ever build *more* than the two agree on, never less.

## What did not change

**A test that did not run still cannot look like a test that passed.** That guarantee never came
from a condition on the job; it comes from receipts. Each module's build records which lane owes its
tests, the test lane records that it ran them, and the check that gates the whole thing fails when
those two accounts disagree — including when both are silent. Moving a module from "tested" to "not
tested" moves it through that same bookkeeping rather than around it.

A pull request that touches a module's code is completely unaffected: it selected and tested exactly
that one module before this change, and it does the same after.

## Both checks were made to fail first

The rules were each deliberately broken and watched go red before being fixed and watched go green —
including the one that guards the *list itself*. A check that walks a list can quietly stop covering
an entry the moment someone deletes it, so a separate assertion pins the entries that were measured:
removing one now turns that assertion red instead of silently shrinking what is checked.
