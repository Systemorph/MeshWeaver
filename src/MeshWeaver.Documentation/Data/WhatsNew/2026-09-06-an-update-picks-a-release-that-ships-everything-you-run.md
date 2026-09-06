---
Name: An update now picks a release that ships everything you have installed
Category: Feature
Description: Instead of taking the newest build and discovering afterwards that a plugin is missing from it, an installation now chooses the latest release that ships every package it has installed — and says which plugin is holding it back when none does.
Icon: CloudArrowUp
Order: -20260906
---

Until now an installation took the newest published build and only found out during start-up whether
every plugin it has installed was actually published for it. When one was not, the new version
refused to come up and the update stalled — after the fact, and with the previous version still
serving.

An installation now asks the question the other way round: which is the *latest* release that ships
all of my plugins? It rolls to that one, so a build that is missing a package is simply passed over
instead of being started and refused. When no published release ships everything, nothing is
selected and the missing plugin is named rather than left for someone to find in a log.

Two things follow from the same change. An installation that cannot see its own plugin list no longer
treats that as "nothing to check" and quietly takes the newest build — it stops and says so. And
anyone rolling a version by hand, or from a delivery pipeline, can now ask an installation which
release it *should* be on rather than only whether a version they already picked is safe.
