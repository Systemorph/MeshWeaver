---
Name: A platform change now runs the plugin suites before it merges
Category: Feature
Description: A platform pull request that changes public code now asks the plugin repository to build and test against it, and carries the answer as its own check. The break that used to appear hours later on other people's pull requests now appears on the one that causes it.
Icon: Link
Order: -20260903
---

# A platform change now runs the plugin suites before it merges

The plugin repository compiles its code against the platform. When the platform removes something
the plugins still use — a type, and since yesterday also a single field or method of a type that
stays — the plugins stop building. Until now the platform had no way to see that: its own checks do
not build the plugins, and the plugins' checks build against the platform's main branch, so the
failure appeared there, later, on pull requests that had nothing to do with it. On 2 September a
two-line removal did exactly this: every plugin pull request was red for three hours, and the job
that went red reported that *nothing was tested* — an entire shard's suites silently stopped running
for everyone.

**A platform pull request that changes public code now runs the plugin suites against itself.** The
plugin repository is asked to build its code and run its portal-host tests against the pull request's
merge result, and the outcome comes back as one check on the platform pull request — green only when
the plugins passed against exactly that change. A pull request that changes no public code says so
and runs nothing. A pull request the plugins cannot answer does not pass quietly; it fails and says
that nobody answered.

Two smaller things landed with it. The check that asks a removing pull request to name its
counterpart now notices a removed *member* as well as a removed type. And a waiver that rests on a
search of the live mesh must quote that the search actually ran — a search that answered *"nothing
was searched"* had been read as *"nothing found"*.
