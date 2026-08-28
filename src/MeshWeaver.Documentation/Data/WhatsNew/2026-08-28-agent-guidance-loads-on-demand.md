---
Name: Agent guidance loads on demand
Category: Feature
Description: The instructions every AI session reads at startup are 60% shorter, with the detail moved into skills that load only when a task needs them.
Icon: Sparkle
Order: -20260828
---

# Agent guidance loads on demand

Every AI session working on this platform starts by reading one file of house rules. It had grown to
about a hundred kilobytes — worked examples, past incidents and command transcripts alongside the
rules themselves — and all of it was loaded before the assistant had even seen the question.

That file is now about 40 KB and holds the rules only. The evidence behind each rule moved into
topic skills — worktrees, pull requests, CI, releases, deployment, testing, mesh data access, async,
UI and localization — which an assistant loads when the work actually calls for them.

The practical effect is a faster, more focused start to every session, with more of the assistant's
attention left for your request instead of for background reading. No rule was dropped in the move.
