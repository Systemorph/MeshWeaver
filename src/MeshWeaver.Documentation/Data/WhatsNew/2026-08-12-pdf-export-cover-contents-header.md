---
Name: PDF export is printed by the browser now
Category: Fix
Description: Exported PDFs keep their cover page, contents, running header and page numbers — and gain repeated table headers — but the contents list no longer prints page numbers.
Icon: Sparkle
Order: -20260812
---

# PDF export is printed by the browser now

Exporting a document to PDF used to draw the page with a separate PDF engine. It is now
printed by the same headless browser that already produced pixel-faithful deck exports, so
the portal has one PDF renderer instead of two.

Everything the export promised still arrives: the branded **cover page**, the **table of
contents**, the **running header and footer**, the **page numbers**, and the page-break rules
that start each chapter on a fresh page. Two things are better than before — a table that
spans pages now **repeats its header row**, and a heading is no longer left stranded alone at
the foot of a page.

**One thing is gone, and it is worth naming:** entries in the table of contents no longer show
the page they point at. They are still real links — clicking one jumps to that section — but
the `..... 12` column is not there. Browsers provide no way to read a target's page number
while laying the document out, and guessing it would risk printing numbers that are quietly
wrong, which is worse than not printing them.
