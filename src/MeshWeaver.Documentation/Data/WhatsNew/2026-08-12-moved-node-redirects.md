---
Name: Moved pages keep their links working
Category: Feature
Description: A new Redirect node left at a retired path forwards the whole subtree to its new home — and a URL that points at nothing now lands on the nearest page that exists, instead of a dead end.
Icon: Link
Order: -20260812
---

# What's New — 12 August 2026

## Moving content no longer breaks every link to it

Until now, moving or merging a section of the mesh silently broke everything that pointed at it: bookmarks, links inside other pages, search results people had saved, references from other repositories. There was no way to say "this lives over there now".

You can now leave a **Redirect** behind. It is an ordinary node placed at the old path that names the new one — and by default it covers **the whole subtree**, so one redirect at the top of a retired section keeps every deep link under it working. Follow an old link and you land on the new page, with the address bar updated to the new location and a short notice telling you where you came from.

Redirects can be chained (an old path pointing at another old path resolves through to the final destination in one step), and a redirect that points in a circle or at something that no longer exists shows a page naming the intended destination rather than failing silently.

**A redirect never grants access.** Following one puts you at the new path exactly as if you had typed it, so the destination's own permissions still apply in full — if you could not read it before, you cannot read it through a redirect either.

## A missing page now sends you somewhere useful

If a URL names something that is not there — a page that was deleted, a mistyped address, a link that was always wrong — you now land on the **closest existing page above it** with a note explaining that the page you asked for does not exist, instead of a dead "page not found".

This only happens when the page genuinely is not there. If a page exists but you are not allowed to see it, or something went wrong loading it, you still get that answer, with its real reason.
