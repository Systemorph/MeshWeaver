---
Name: Pinning a type to an earlier release works again
Category: Fix
Description: A type pinned to one of its historical releases was refused on every portal since this morning's build, with a message about being "built against framework 3.0.0.0"; the pin is honoured again, and a release that predates the identity record is adopted with a note rather than refused.
Icon: Checkmark
Order: -20260907
---

# Pinning a type to an earlier release works again

A type can be pinned to one of its earlier releases — the record of a specific build — so that its
instances keep running that build while newer ones are compiled. From this morning's platform
build every such pin was refused, and the page showed an error saying the release was "built
against framework 3.0.0.0" while the portal "runs" a long identifier.

The check that refuses builds from another platform generation was reading the wrong field: the
release's version number, not the record of which platform build produced its assembly. Those two
values can never agree, so no pin could pass. The check now reads the assembly's own build record,
and behaves as intended: a release built for another platform generation is still refused with
both identities named, while a release from before that record existed is adopted with a note in
the log asking for a fresh release, instead of being refused.
