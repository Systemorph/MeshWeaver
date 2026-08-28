---
Name: A playbook for host crashes
Category: Feature
Description: The steps for diagnosing a crashed process are now written down, so the same crash is not re-investigated from scratch.
Icon: Sparkle
Order: -20260828
---

# A playbook for host crashes

Occasionally a process running the platform stops with a crash rather than a normal error. Those
are the hardest failures to work on, because the crash lands wherever the program happened to be
rather than where the fault is — so the obvious suspect is usually innocent, and past
investigations repeatedly reached for the same wrong explanation.

The accumulated findings from those investigations are now written down as a single procedure: how
to tell a genuine crash from an ordinary error wearing the same exit code, which evidence a failed
run actually carries, and the two underlying causes that account for the crashes traced so far.

Nothing about the running platform changes. What changes is that the next crash of this kind
starts from what was already learned instead of from the beginning.
