---
Name: Plugin pages stop timing out on themselves
Category: Fix
Description: Installed plugins such as courses could become unreachable, retry forever, and never recover on their own.
Icon: Sparkle
Order: -20260810
---

# Plugin pages stop timing out on themselves

An installed plugin — a course area, a skills or agents section — could stop opening altogether.
The page never rendered, tools reading the same content answered "unavailable", and the portal kept
retrying without ever getting further. Nothing was actually broken: the content was there, and the
part of the portal that starts a section up was working on it.

The cause was a section asking the portal for its own page while it was still starting up. That
question could only be answered once start-up had finished, so it always ran out of time — and the
portal treated running out of time as proof that the section had failed. Start-up was abandoned at
exactly the moment it was about to succeed, and each retry inherited the same stale answer, so the
section could never get going again. It hit hardest where a section legitimately takes a little
longer to prepare, which is precisely where the extra patience was designed to go.

Now a section no longer treats a question it can only answer itself as a verdict on whether it
started. Sections that need longer to prepare are given that time, a genuinely broken one shows the
explanatory page it was always meant to show, and a section that is missing is reported straight
away instead of after a wait. Recovery no longer needs a restart.
