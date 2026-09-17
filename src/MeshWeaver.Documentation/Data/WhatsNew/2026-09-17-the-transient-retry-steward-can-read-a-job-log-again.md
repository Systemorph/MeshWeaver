---
Name: The automatic retry for infrastructure blips can read a build log again
Category: Fix
Description: The automation that re-runs a build whose only failures were registry or GitHub outages had been unable to read a single log since at least 2026-09-10, and declined every time in a sentence that read like a deliberate decision — it now reads the log directly, and reports a loud failure instead of a quiet decline when it cannot.
Icon: ArrowSync
Order: -20260917
---

# The automatic retry for infrastructure blips can read a build log again

A build whose only failures are infrastructure — the container registry refusing a connection
mid-login, GitHub's own token service answering 500 — is supposed to get exactly one automatic
retry, so that a 25-minute cycle is not lost to a blip somebody has to notice and re-run by hand.

For at least a week that automation read nothing at all.

It asks for each failed job's log and looks for a small, curated list of named infrastructure
signatures. It fetched those logs with the GitHub CLI, and a newer CLI on the build machines began
refusing to print any response containing terminal colour codes. Every build log contains them —
each command is echoed back in colour — so the read could never succeed. The automation then said:

> log unreadable (the response contains terminal escape sequences…) — cannot prove a transient, no retry.

…and finished green. Read quickly, that is a considered judgement about a build. It was nothing of
the kind: the automation had gone blind, and its "no retry" carried no information about any build.
Nobody noticed for a week, because a decision that is never made looks exactly like a decision to
do nothing.

Two things changed. The log is now fetched straight from the API rather than through a command-line
tool, so a tool's display policy can no longer decide whether a build gets retried. And an
unreadable log is now a **loud, named failure** instead of a quiet decline — if this automation
cannot see what it is judging, it says so where somebody will see it, rather than reporting a clean
decision over an empty hand.

What has deliberately *not* changed is what gets retried. The signature list is unchanged, and a
build with even one failure that is not a named infrastructure signature is still never retried —
verified against the real builds from 2026-09-16, which contained the registry outage and are still
correctly declined. Restoring the retry must not turn into retrying genuine failures.
