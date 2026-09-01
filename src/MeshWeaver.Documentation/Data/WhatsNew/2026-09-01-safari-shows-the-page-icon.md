---
Name: Safari now shows a page's own icon in the tab
Category: Fix
Description: Node pages served their icon as SVG, which Safari does not render — so on Mac and iPhone every tab wore the portal mark. Each page now also offers a PNG, and a bookmark tile to match.
Icon: Sparkle
Order: -20260901
---

# Safari now shows a page's own icon in the tab

Open a package, a space or a document and the browser tab shows **that page's own mark** — the
chess board for Chess, each plugin's own icon — instead of the portal logo repeated across every
tab. That has been true since August, on Chrome, Edge and Firefox.

**On Safari it was never true.** Pages offered their icon in SVG form, and Safari does not render
an SVG favicon at all — so on every Mac and iPhone the tab quietly fell back to the portal mark,
whether you were signed in or not.

Each page now offers its icon **both ways**: the scalable original, and a PNG rendered from exactly
the same artwork. Browsers that read SVG keep the crisp one, and Safari takes the PNG — so the tab
strip finally reads the same everywhere. Safari's larger tile — the one you get bookmarking a page,
adding it to the Dock, or pinning it to the Start Page — now carries the page's mark too, instead
of Safari's own letter square.

Nothing is invented for a page that has no mark of its own: it keeps the portal icon, exactly as
before. And nothing changes for a page whose icon was already a picture file — those always worked.
