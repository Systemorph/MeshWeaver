---
Name: Errors in production open their own tickets
Category: Feature
Description: A watcher reads the platform's error logs, an agent works out what broke, and each distinct fault gets exactly one GitHub issue.
Icon: Bug
Order: -20260807
---

# Errors in production open their own tickets

Until now, an error logged by a running portal sat in the logs until somebody went
looking for it. Now it reports itself.

A small watcher reads the platform's error logs continuously and groups them by what
actually went wrong. The first time a distinct fault appears, an AI agent reads the
evidence, works out which component is broken and why, and opens a GitHub issue in the
repository that owns that code — with the stack trace, how often it has happened, and
where to start looking.

**A burst of ten thousand identical errors is one ticket, not ten thousand.** Errors are
matched on the fault itself, not the text of the line, so the same failure hitting
different users, different records, or different servers still lands on one issue. When
that fault happens again later, the existing issue gets a short "still occurring" note
rather than a duplicate — and if the issue had already been closed, it reopens.

Every fault is also visible inside the platform, with a link to its ticket and to the
agent's reasoning, so you can see what has been reported and what is still open.

The watcher runs separately from the portal on purpose: the thing that notices the portal
is unhealthy keeps working even when the portal is not, and reports what it saw once the
portal is back.

Nothing is reported until an administrator configures where tickets should go, so the
feature stays off until it is deliberately turned on.
