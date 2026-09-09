---
Name: A node page says when it last changed — and a container says when anything under it did
Category: Fix
Description: The Type · Created · Updated line was only ever drawn by pages using the framework's own overview, so a node type that renders its own page shipped without it. Modules can now draw the same line, and a container page can report the newest activity beneath it instead of its own row.
Icon: Clock
Order: -20260909
---

# A node page says when it last changed

Every Markdown node carries a provenance line — `Type: Markdown · Created: … by … · Updated: … by …`.
Node types that render their **own** default area did not, and the reason was structural rather than
deliberate: the line rides on the framework's page header, and a type that draws its own page never
asked for it. The information was on the node the whole time, one click away under Settings.

Two things change.

**The line is now a module-facing control of its own.** `MeshNodeLayoutAreas.BuildMetaRow` renders it
for any page that wants the standard provenance without also taking the icon/title/action block it
has already drawn itself. `BuildMetaEntries` behind it is pure — node, viewer's zone, no host — so
what the line says can be asserted without standing up a mesh.

**A container can report the activity beneath it.** On a node whose subject is a partition rather
than a document, the node's own `LastModified` is not merely incomplete, it is misleading: a CRM
client root is written when the account is opened and then barely again, so its own row reports
whatever last touched the ROOT — on a retyped client, the migration, stamped `system-security` —
while the deals and documents underneath moved days later. "Updated 2026-08-28 by system-security"
on a page whose account was worked yesterday is a wrong statement, not a missing one. Passing a
`NodeActivity` replaces that segment with `Last activity: … by … — …`, and it **replaces** rather
than joins it: two last-changed answers on one line, one of them a migration's, is worse than the
weaker of the two. With no activity recorded underneath, the line falls back to the node's own row,
so an empty container does not read as one that never existed.

The new `Last activity:` label is translated like the four already on the line, and so is the word
joining a timestamp to whoever made it. That last one took some care: the part of this that can be
tested without a running mesh is deliberately free of any connection to the viewer, and a translated
word cannot be chosen without knowing who is reading. So the timestamp, the person and the
description travel separately and are only joined when the line is drawn — which is the moment the
viewer's language is known.
