---
Name: Decks can now export exactly as they look
Category: What's New
Description: A new pixel-faithful option on Export to PDF renders a deck in a browser, so gradient and image backgrounds, slides written in raw HTML, and CSS layout land in the PDF exactly as they appear on the stage.
Icon: Sparkle
---

# Decks can now export exactly as they look

Exporting a deck to PDF has always been *content*-faithful: every slide's words,
lists and tables reach the file, one page per slide. For most decks that is the
better artifact — it is small, it is quick, and you can select and search the
text.

But a deck whose point *is* how it looks came out flattened. A slide with a
gradient background printed on white. A slide written as raw HTML — the way
design-led slides usually are — lost its layout. Anything carried by CSS
(background images, custom positioning, rotations) simply had nowhere to go,
because the PDF was being rebuilt from the slide's text rather than from the
slide as drawn.

**Export to PDF** on a deck now offers a choice:

- **Content-faithful** — unchanged, and still the default. Fast, small,
  selectable text, and it needs nothing installed.
- **Pixel-faithful** — the deck is laid out in a real browser, on the same 16:9
  stage and with the same styling the Present view uses, and printed from there.
  Gradients, background images, raw HTML, CSS layout and transforms all arrive
  exactly as they look on screen. Images stored alongside your slides are
  embedded in the file, so the PDF stands on its own.

The trade is worth stating plainly: a pixel-faithful export takes longer, the
file is larger, and the text in it is a picture of text rather than text — so it
cannot be selected or searched. Reach for it when the deck is going to a client
or a stage; keep the default when the deck is going to a reader.

## When you will see the option

The choice appears only on decks, and only where the platform can render one —
it needs a headless browser available to the server. Portals that have one show
both options; portals that do not simply show no choice and continue to export
content-faithfully, exactly as before. Nothing about the existing export
changed, and no export you run today behaves differently unless you pick the new
option.

If you administer a portal and want to offer this, see
[Pixel-Faithful Export](/Doc/Architecture/PixelFaithfulExport) for how to point
the platform at a browser.
