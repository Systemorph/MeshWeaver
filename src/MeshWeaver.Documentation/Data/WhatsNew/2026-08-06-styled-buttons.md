---
Name: Buttons can be styled again
Category: Fix
Description: A button's style and class now reach the rendered control, so a page can paint its call-to-action in its own colours instead of the default accent.
Icon: PaintBrush
Order: -20260806
---

# Buttons can be styled again

Setting a style or a class on a button now takes effect. It never did: the value was carried all the way to the view and then dropped, so a page that asked for a bold, differently-coloured button silently got the standard one. Every other control honoured the same setting, which made the gap easy to miss — the code looked right and the button simply ignored it.

The visible effect is on covers. A course or plugin cover can now show one call-to-action row in its own palette, with the install step sitting in it as a proper button rather than a separate strip above the page. Buttons that never asked to be styled are unchanged.

If you write layout areas: a button paints its surface inside a web component, so plain colours set on the element cannot reach it. Add the `cta-pill` class and pass your colours as the `--cta-bg` and `--cta-fg` custom properties, which do cross that boundary; both fall back to the standard accent when you leave them out.
