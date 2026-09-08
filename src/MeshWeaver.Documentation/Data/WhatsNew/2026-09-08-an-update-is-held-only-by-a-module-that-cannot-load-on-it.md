---
Name: An update is held only by a module that cannot load on it
Category: Feature
Description: Your installation now updates unless one of your modules provably cannot load on the new build — measured on the module's own bytes against what the new build carries. A course or package with no prebuilt bake for that build compiles when the new build starts instead of holding the update, and the Updates tab tells you which ones will.
Icon: ArrowSync
Order: -20260908
---

# An update is held only by a module that cannot load on it

Until now an update could be held for two reasons that had nothing to do with whether it would
work. A module could *declare* a platform version the new build's version string did not rank
above — fixed earlier this week. And a course or package could lack a *prebuilt bake* for the new
build: the update waited until every one had been rebuilt for it, although the installation
compiles such content itself when it starts, exactly as it does on every pull request that ships
it. On 7 September both rules together kept every production installation on its morning build for
the whole day while every candidate would have run fine.

**What holds an update now is measured, not declared.** For every module you have installed, the
platform asks the new build one question: *would the bytes you are running load there?* Each build
publishes the list of types it carries; the platform links your module's actual bytes against that
list before it rolls. A module that references a type the new build does not have — the only way
an update can take away something that works today — holds the update, and the hold names the
module and the missing type. A module the new build ships a fresh version of is adopted at the
roll and needs no check.

**A missing bake is a cost, not a hold.** If a course or package has no prebuilt bake for the new
build, the update proceeds and the content compiles when the new build starts. The Updates tab
tells you in advance which ones will (*"would recompile at boot: AgenticPrimer, Crm"*), so a slower
first start is never a surprise. Installations that must never compile content at start
(`Modules:RequirePrebuilt`) keep the previous behaviour: for them a missing bake still holds.

**When the platform cannot tell, it says so and does not guess.** A build published before this
change carries no type list, so a landed module cannot be checked against it. That is reported on
the Updates tab as *"could not be determined"* — it neither clears the update nor holds it. The
platform then decides at start-up, on the same measurement, and keeps the previous version of a
module whose new one cannot load.

See [The Module Platform Link Gate](/Doc/Architecture/ModulePlatformLinkGate) for how the
measurement works and [Release Availability Gates](/Doc/Architecture/ReleaseGates) for what the
gate reports.
