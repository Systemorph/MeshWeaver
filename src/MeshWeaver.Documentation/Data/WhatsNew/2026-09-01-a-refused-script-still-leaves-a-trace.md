---
Name: A script refused before it starts still leaves a trace
Category: Fix
Description: The fast refusal for a script that cannot run now writes the same warning the slow path always did, so an operator can still find out why a run never happened.
Icon: Sparkle
Order: -20260901
---

# A script refused before it starts still leaves a trace

Asking to run a script on something that cannot run one — a page that is not a script, a path with
nothing behind it, a script switched off — now answers immediately instead of waiting out the whole
dispatch budget. That is a good change, and it stays.

It had one cost nobody could see. The slow path used to leave a **warning in the log** on its way
past, and that warning is the only evidence anywhere that a run was asked for and never happened:
no run means no activity to look at afterwards. Answering earlier skipped the place the warning was
written, so the caller got a clear message and the server went quiet — which is precisely the
situation the warning was added for in the first place.

The fast refusal now writes the same warning, naming the path and what was wrong with it. Nothing
changes for the person who made the call; what changes is that whoever looks at the logs afterwards
can still see it happened.
