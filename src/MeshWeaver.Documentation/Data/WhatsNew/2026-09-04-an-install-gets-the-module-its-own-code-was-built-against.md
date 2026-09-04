---
Name: An install gets the module its own code was built against
Category: Fix
Description: A plugin download could carry compiled types built against one build of a shared module beside a different build of that module, so the portal that installed it declined every one of those types and rendered the plugin's pages empty. The download now hands over the build its own code records.
Icon: LinkMultiple
Order: -20260904
---

# An install gets the module its own code was built against

Installing or updating a plugin fetches one archive: the plugin's compiled types, plus the compiled
module they depend on. The two halves came from two different places. The types were resolved for
exactly the platform build the installing portal runs. The module was whatever the module's own
release published last — a version number that does not move when a rebuild changes the bytes.

Most of the time those agree. When they did not, nothing failed loudly: the download succeeded, the
archive was well formed, the module landed, and then the portal declined every compiled type in the
package — *"dependency record mismatch — built against …, live is …"* — and quietly recompiled them
itself, or, on a deployment that does not compile plugin content, served the plugin's pages empty.
That is what memex.meshweaver.cloud did to one plugin's four packages on 2026-09-03, with no change
in any repository to point at.

The archive already knew the answer. Every compiled type in it records which build of the module it
was compiled against, so the download now hands over **that** build: the registry's own copy when the
registry's copy is it, and otherwise the module bundle sealed for the installing portal's platform
alongside the types themselves. Nothing else changes — a package whose types bind no module, and the
ordinary case where the two already agree, take exactly the path they took before.

When no available build matches, the download says so in the package manifest, naming both builds,
instead of leaving it to be discovered as a decline in the installing portal's log hours later.
