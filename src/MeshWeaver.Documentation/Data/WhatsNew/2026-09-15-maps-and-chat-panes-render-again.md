---
Name: Maps and chat panes render again
Category: Fix
Description: Every view pack installed from the Store shipped without the JavaScript its views load, so maps came up blank and chat panes lost their editor behaviour. The file now rides the package, and the build refuses to produce one that is missing it.
Icon: Map
Order: -20260915
---

# Maps and chat panes render again

A map placed on a page came up **blank**. Not an error, not a broken-image box — an empty frame,
every time, on every page, for every map. The same silence hit the chat pane's message list and
thread view: they rendered, and then simply did not behave.

What they have in common is how a view pack is written. A Blazor view that needs a little JavaScript
keeps it in a file beside the component — `OpenStreetMapView.razor.js` beside
`OpenStreetMapView.razor` — and asks the browser for it the moment it first renders. That file was
**not in the package**. The browser asked, got a 404, and the view stopped where it was.

Everything around it looked healthy, which is why it lasted three weeks. The package installed. The
module loaded. The *other* files in it — 424 KB of map library, the pane's stylesheet, its
background image — were served correctly out of the very same folder. Only the one file each view
actually imports was absent, and nothing anywhere logged a word about it.

**Four packs were affected — OpenStreetMap, Google Maps, Apple Maps and Chat — and every deployment
that installed one inherited the same blank frames.** Reinstalling did not help; the file was never
in any copy of the package.

## What changed

The tool that compiles a view pack into a package now carries that file, at the path the browser
asks for. Two guards came with the fix, because the failure had no symptom anyone could have
noticed:

- **The build refuses a pack whose JavaScript pairs with nothing.** A component renamed without its
  script used to produce the same blank view; now it stops the build and names the file.
- **The packaging step checks every file, not the count.** The old check asked only whether a
  package declared *some* assets — and OpenStreetMap's map library answered yes while the file its
  view imports was missing. Apple Maps, whose entire asset is that one file, was not checked at all.

No action is needed: the next version of each pack carries the file, and installs pick it up
automatically.
