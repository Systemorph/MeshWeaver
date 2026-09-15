---
Name: Your node's icon stays visible — in the app and in the browser tab
Category: Fix
Description: An icon authored as a plain outline was invisible on one of the two themes on five surfaces of the portal, and in the browser tab as well. Every place the platform draws an inline-SVG icon now puts it on a backplate.
Icon: ColorBackground
Order: -20260915
---

# Your node's icon stays visible — in the app and in the browser tab

Icons are normally authored as a **monochrome outline that takes its colour from the surrounding
text** — `stroke="currentColor"`, the shape nearly every icon set uses. The platform has a rule for
exactly that case: an icon with no background of its own gets one generated for it, in a colour
derived from the icon itself, with the glyph recoloured white so it reads on a light theme and a
dark one alike. An icon that already paints its own background — every store mark, every thread
identicon — is left exactly as authored.

**The rule ran in one place.** Five others drew the icon as it stood:

- the icon tile at the head of every node page,
- the icon beside the title on the overview,
- the icon a node draws when its content refers to itself,
- the preview in the create form,
- and the preview in the icon picker — so the picker showed you something different from what the
  portal would actually draw.

On the theme the icon was not authored for, it was simply not there. Not a broken-image box, not an
error: a blank square. Around twenty nodes of one Space read that way in dark mode, and the only
workaround was to re-author every icon by hand with its own background and white detail.

## The browser tab, where nothing looked wrong at all

The same icon is lifted into the page head, so a node's own mark shows in the tab strip and in a
link preview — and, for Safari (which reads no SVG favicon at all), it is rendered to a PNG by
`/api/icon/…`. Both of those draw the icon **on its own**, with no page text around it, so an icon
whose colour is "whatever the text around me is" has nothing to take a colour from and comes out
**black**. The renderer, meanwhile, deliberately leaves its background transparent — it was written
on the understanding that every mark reaching it carries its own.

The result was a black hairline on nothing: invisible in a dark tab strip, invisible on a dark
link-preview card, and served as a perfectly valid image, so nothing anywhere reported a problem.
Both consumers now receive the icon through the same rule as the app, so the tab, the card and the
page cannot disagree about what a node looks like.

## What changes for you

If your icons already carry their own background, **nothing**: the background, the colours and the
artwork you authored are left exactly as they are, on every surface. If you re-authored an icon to
work around this, it keeps rendering as you wrote it. And an outline icon you had given up on now
shows up — on both themes, in the tab, and in a link preview.

Two tests keep it that way, and they are deliberately different in kind: one drives each surface
with the icon shape that vanished and reads the markup it produced, and one derives the list of
surfaces **from the source files** rather than from a list someone has to remember to extend — a
hand-written list would have named the single surface that was already correct, which is how this
went unnoticed in the first place.
