---
Name: Update preparation now reads all of your content
Category: Fix
Description: The faster update-preparation path used to read only the first page of source content on a large mesh; it now reads all of it, and refuses to guess when it cannot.
Icon: Sparkle
Order: -20260811
---

# Update preparation now reads all of your content

When the platform updates, it prepares every content type on your mesh so the first person to open
a page does not have to wait. The faster preparation path introduced earlier today asked the mesh
for its source content in a single request — and on a large mesh that request came back with only
the first page of results. Types whose content sat beyond that page were prepared against nothing,
and then reported as broken even though nothing was wrong with them.

The request now asks for everything, and — this is the part that matters — it **checks** that it
got everything. If the answer could be incomplete, or if a type that says it has source content
comes back with none and nothing else on the mesh agrees, preparation stops and hands the whole
update back to the slower, proven path. It no longer reports a type as broken on evidence it never
actually gathered.

You would have seen this as an update that took much longer than usual, or as content types
briefly showing a preparation error after a platform update. Nothing was lost either way: the
update safety-check kept the previous version serving.
