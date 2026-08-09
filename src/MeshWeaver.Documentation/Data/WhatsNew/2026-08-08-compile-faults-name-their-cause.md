---
Name: A failed build now says what actually broke
Category: Fix
Description: When a build fails for an infrastructure reason rather than a mistake in your code, the failure now names the fault instead of showing a bare message with no cause.
Icon: Bug
Order: -20260808
---

# A failed build now says what actually broke

Almost every failed build is a mistake in the code, and those have always read
well: the compiler's own diagnostics, each one pinned to its line.

The rare exception was the confusing case. When a build failed for an
infrastructure reason — something going wrong *before* the compiler ever ran —
the build status showed only that fault's bare message. The most common one
reads `Object reference not set to an instance of an object.`, which tells you
nothing at all, and in particular does not tell you the one thing that matters:
that the problem was not in your code and there is nothing in your code to fix.

A build that fails this way now names the fault. Diagnosing one no longer
depends on reproducing it — the details are recorded when it happens, which for
a fault that appears only occasionally, and only under load, is the difference
between a fixable report and a guess.

Nothing changes for an ordinary failed build. Compiler diagnostics are still
what you see, still pinned to their lines, and the build status still shows them
first.
