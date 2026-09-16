---
Name: A node page now carries who wrote it, by default
Category: Fix
Description: The Type · Created · Updated line rode on one renderer, so a node type that drew its own landing page dropped it without a trace. A census found 86 of the 99 such pages across the fleet had. The default is now the other way round, and a page that wants no provenance line has to say why.
Icon: DocumentBulletList
Order: -20260916
---

# A node page now carries who wrote it, by default

Every node page shows a provenance line under its title — `Type · Created · Updated`, in your own
time zone, with the author beside each stamp. Except that it only ever came from one renderer: the
framework's standard page. A node type with a designed landing page of its own replaced that
renderer, and the line went with it.

Nothing said so. The information was on the node the whole time — a Space could carry
`createdBy: rsalzmann`, `lastModified: 2026-09-15T07:17:01Z` — and only the header omitted it. So a
reader of one of those pages could not tell who had written the thing they were looking at, or when.

Worse, an omission somebody had **weighed** looked identical to one nobody had thought about. An
`Activity` page reports `Compilation · Succeeded · started 09/16 11:56 · ended —`, which answers the
question better than `Created`/`Updated` would; a `Thread` answers it per message. Those are good
decisions, and they were indistinguishable from an oversight, because both are just an absence.

Counted across the fleet on 2026-09-16: of the 99 landing pages that replace the framework's
renderer, **86 had lost the line** — four here, eighty-two in the plugin repositories. At that ratio,
adding it back page by page just leaves the next one to be forgotten.

So the default changed. A landing page registered without an opinion about provenance now **gets**
the line, composed by the framework above the page's own content. A page that draws the standard
header itself says so and keeps exactly what it had. And a page that should carry none has to state
a reason — a blank one is refused — so the decision ends up written down where the next reader
finds it instead of inferred from nothing.

Three pages in the platform gained the line as a result: the **plugin catalog**, **global settings**
and a **partition record**. The **user home** deliberately did not: the subject of that page is a
person, and the node's stamps describe the row — `Updated … by system-security` because somebody
flipped a preference — which is a database fact answering a question nobody asked about a colleague.

Full detail: [Node Page Provenance](/Doc/Architecture/NodePageProvenance).
