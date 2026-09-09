---
Name: A page never dies because its bundle is late
Category: Fix
Description: A module's source could be synced into a portal ahead of the compiled bundle for that portal's platform build, and every page of the affected type then refused to render until the bundle caught up. The portal now keeps serving the last build it holds, says so on the type, and tells you when the new build is adopted.
Icon: ShieldCheckmark
Order: -20260909
---

# A page never dies because its bundle is late

A module's compiled assemblies are baked once per platform build. Its **source** reaches a portal
much faster — within minutes of a merge. Between the two, the portal holds newer source than the
bytes it has, and until now the safeguards that protect you from running stale code combined into
an outage: the bundle was refused because its source fingerprint no longer matched, the local build
was refused because the portal does not compile a module's content it does not track, and the type
settled as an *error* with a perfectly working assembly still on the record. Every page of the type
showed *"the platform refused to run it"*. On 9 September that took `Essentials/Email` down for an
afternoon over a one-line CSS change — a mail that was being prepared for sending could not be
opened.

Three things change.

**The last build keeps serving.** Whether an adopted build may stay is now a *version
compatibility* question, not a fingerprint match. If the adopted build and the current source share
the same module **major** version, the build keeps serving and the type reads honestly:
`compilationStatus: Ok`, `buildProvenance: StaleAdopted`, and the NodeType page says which build is
serving (module version and source fingerprint), which source is current, and that a bundle for the
running platform build is awaited. Only a major bump — a declared incompatibility — refuses the
build, and even then the type reports *"incompatible, awaiting bundle"* rather than an error. A
fingerprint that merely differs still means what it meant: the source moved. It drives the pending
status and the notification below; it no longer drives a refusal.

**You are told when the new build lands.** Adopting a bundle that matches the current source lifts
the hold and posts a notification — *"Essentials 1.2.3 adopted"* — to the person who requested
the release, or to the platform operators when nobody did. Entering the hold is announced the same
way, once. There is no longer a silent window between "merged" and "page dead".

**Nothing is parked as an error for a delivery problem.** A type with nothing to serve at all still
parks with the named reason (that is the truth); a type that holds a build is *held*, not parked,
so the next release request re-runs the cheap adoption check instead of re-serving a cached error.

The versions compared are the bundle manifest's released version (stamped on the type as
`adoptedModuleVersion`) and the partition root's `content.version` (published beside the source
fingerprint as `currentModuleVersion`). A bundle or a root that carries no version compares as
*unknown*, which never refuses.
