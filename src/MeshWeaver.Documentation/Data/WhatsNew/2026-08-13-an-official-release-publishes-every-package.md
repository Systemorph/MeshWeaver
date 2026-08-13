---
Name: An official release publishes every package again
Category: Fix
Description: Cutting an official release stopped halfway — newer components were missing the packaging metadata the publish step insists on, so nothing reached nuget.org. Every component now carries its documentation and ships.
Icon: Sparkle
Order: -20260813
---

# An official release publishes every package again

Cutting an official release — the tagged kind that publishes the platform's packages to
nuget.org — quietly stopped working somewhere between the last release and this one. The
release run went red at the packaging step and not a single package was published.

The cause was growth without ceremony: twenty-one components added since the previous release
never received the short readme that nuget.org displays on a package's page, and the publish
step — correctly — refuses to ship a package that promises a readme and does not contain one.
One component had the opposite problem and packed its readme twice, and the project template
package assembled its content in an order that only worked on a machine that had built it
before, so on a clean release runner it packed empty.

All of it is fixed at the source: every published component now carries a real readme and a
description that will show up properly on nuget.org, the duplicate is gone, and the template
package generates its content before — not after — the packaging step collects it. The whole
publish sequence was replayed locally end-to-end, producing all seventy-six packages, before
this change was allowed in.
