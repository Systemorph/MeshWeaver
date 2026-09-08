---
Name: A content type nobody registered can no longer slip through CI
Category: Fix
Description: The check that stops a page shipping empty because its content type was never registered had no way to fire — the warning it looks for could not reach the file it reads. It can now, and the check keys on the event itself rather than on the sentence describing it.
Icon: Checkmark
Order: -20260907
---

# A content type nobody registered can no longer slip through CI

When a page renders blank, the cause is often not the page. A node's content arrives as JSON that
names its own type, and if the part of the system reading it has never been told about that type,
nothing fails: the value quietly arrives as raw text nobody can use. The view has nothing to show,
a wait for that value never finishes, and there is no error anywhere.

Because it is silent, the only way to catch it is to look for it deliberately. A check has run on
every test batch since early September to do exactly that — and it turns out it could not have
found anything.

## What was wrong

The warning that reports the problem was written in a way that kept it out of the one log file the
check reads. That file deliberately keeps a narrow set of records so it stays readable, and this
warning did not qualify for any of them. So the check scanned a file the warning could never appear
in, found nothing, and reported success on every run — which looks exactly like a check that
passed.

Measured on a run where content really did degrade: **817** records in that file naming the test
involved, and **zero** occurrences of the warning it had emitted.

## What changes

**The warning now reaches the file**, so the check can find it.

**The check now looks for the event, not for the sentence.** It used to search for a phrase from
the warning's text. A phrase is a description — reword it and the check goes on searching for
something nobody writes any more, silently. It now keys on the event's own identity, which the
compiler enforces at every place the event is raised, and which a separate test ties to the
check's copy of the name. The phrase is still searched for as a second net.

**A third place that could go silent was closed too.** One of the three ways content can degrade
worded its warning slightly differently, so the old phrase search would never have matched it
either.

## How we know it works

The check was made to fail and then to pass, on the same real degradation:

- with the fix removed, the record was **never written** and the check reported success;
- with the fix in place, the record was written and the check **failed**, naming the affected node;
- against a log carrying a real but unrelated fault, the check **passed** — so its success is a
  verdict about the content, not the result of finding an empty folder.

## What it found on its first working run

The check went from never firing to firing, and immediately reported something real: **every
deployment's own module-inventory record was unreadable.** Each portal writes a record of what it is
running, and that record was tagged with a type name that nothing in the product defines — so
whatever read it back got nothing. The tag had been added on purpose, with a note saying it was
there to stop exactly this; it named a type that did not exist, so it never worked. Nothing could
see that until now: the record was stored perfectly, and only its *readers* came up empty.

Both halves are fixed — the record's type is registered, and the tag is derived from it rather than
typed out by hand, so the two cannot drift apart again.

A new test drives the real code path and evaluates the log file's own admission rule against the
record it produces, so this cannot quietly come undone again.

## The check has no exemption list, and it still does not

Once the check could see this problem, it also started seeing tests that *cause* it deliberately —
tests whose whole point is that a piece of content cannot be read, so that the product can be shown
to recover once the missing type arrives. Turning those off with an exemption list was refused: a
list of "ignore these" is a check that can go green for the wrong reason and never tell you.

The advice the check gives — build the fixture out of a *different, readable* type — fixes the
common case, and it did fix one test. It cannot fix a test whose entire subject is content that
nothing can read.

So those tests now keep their own record instead, and **prove they got one**. Each one asserts that
the product really did report the problem, that it named the right node, and that the report was of
the kind the log file would have kept. Anything else the test happens to cause is still reported
normally. The result is the opposite of an exemption: where before these tests wrote a report into
a file nobody read, they now fail if the report stops being produced at all.

That was checked by breaking it on purpose: with the original defect put back, all three tests fail,
saying the product no longer reports the problem they exist to reproduce.

## One thing it still cannot tell you

A content type that is not known *yet* and a content type that will never be known look identical at
the moment of reading. The first is ordinary — it happens every time a portal starts, before the
types it compiles at runtime are ready, and the product recovers on its own moments later. So a
report from this check means "this content could not be read at the moment it was read", not
necessarily "this is broken". Confirming which one it is means looking at whether the type in
question ever finished compiling. Telling the two apart automatically is a larger change and is
tracked separately.
