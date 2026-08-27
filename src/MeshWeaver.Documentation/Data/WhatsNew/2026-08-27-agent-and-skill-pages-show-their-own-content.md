---
Name: Agent and Skill pages show their own content again
Category: Fix
Description: Fixed the built-in type definitions taking over the /Agent, /Skill, /Harness, /Model and /Provider pages, which made them render a bare placeholder instead of the published package.
Icon: Sparkle
Order: -20260827
---

# Agent and Skill pages show their own content again

Opening **/Agent** or **/Skill** showed a bare page: the right name was missing, the
description was missing, and a link to either page shared as an empty card on Slack, Teams
or LinkedIn. The published packages behind those pages were fine the whole time — nothing
was reading them.

Five top-level pages share a name with something the platform defines internally:
**Agent**, **Skill**, **Harness**, **Model** and **Provider**. A deployment says whether it
stores that content in its database, and when it does, the internal definition steps aside
and the stored content owns the page. A recent internal change stopped that setting reaching
the part of the platform that reads it, so every deployment behaved as if it had no setting
at all: the internal definition took the page and the stored content became unreachable —
not just outranked, but invisible to the page, to search, and to anything trying to update
it.

The setting now reaches it on every deployment, and a page can no longer be quietly taken
over this way. Two related effects are fixed with it: content for those five areas is
imported and kept up to date on start-up again, and the model **Provider** area — which had
never been created on affected deployments — now appears.

If you maintain a deployment: nothing to change. Your existing configuration was always
correct; it simply was not being read.
