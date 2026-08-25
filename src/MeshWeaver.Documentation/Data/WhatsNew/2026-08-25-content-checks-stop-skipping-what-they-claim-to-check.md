---
Name: Content checks stop silently skipping the content they claim to check
Category: Fix
Description: A check that reported success over zero items now reports what it actually examined, and the pension-fund sample tree — 72 files whose types were never once checked — is checked like every other tree.
Icon: Bug
Order: -20260825
---

# Content checks stop silently skipping the content they claim to check

Before anything ships, the platform's own sample and documentation content is compiled, rendered
and run — the same way a portal will run it. That check is what stops a broken page, a view that
renders empty, or a type that will not build from reaching anyone.

It was passing over content it never looked at.

**A whole sample tree was reported as fine without a single check.** Text files can begin with an
invisible marker that says "this file is UTF-8". The importer that installs content learned to
ignore that marker; the checker that decides *what to check* did not, so it quietly discarded every
file carrying one. The pension-fund sample tree — 72 files, including all five of its type
definitions — carries that marker throughout. The result read as a clean pass over 72 files and
zero types: a green tick asserting a measurement nobody had taken. Those five types are now checked
like every other tree's, and the two genuine problems that were hiding behind the green tick are
recorded as known work rather than left invisible.

**The checker and the builder now agree on what counts as content.** The two halves each decided
independently which files were real content, and could disagree in both directions — a type the
builder shipped but nothing ever checked, or a type the checker waited on that was never installed
at all, which showed up only as an unexplained timeout. Both now ask the installer, which is the
one authority on the question, and a test holds the two answers equal so they cannot drift apart
again.

**Checks now prove they examined the shipped result.** The step that produces the compiled content
and the step that judges it were one and the same, so what was rendered and tested was a private
rebuild rather than the thing that ships. They are now separate: the content is built once, and the
checks run against exactly those results — and the run fails if it turns out to have quietly
rebuilt its own copy instead, or to have judged nothing at all.
