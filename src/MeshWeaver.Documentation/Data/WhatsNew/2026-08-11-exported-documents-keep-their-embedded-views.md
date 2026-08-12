---
Name: Exported documents keep their embedded views
Category: Fix
Description: Exporting a document to PDF or Word used to lose every embedded view — a card grid, a table, any @@(…) area — printing the reference's raw source text instead. Embedded views are now rendered into the exported file, and a view that cannot be rendered says so instead of vanishing.
Icon: DocumentPdf
Order: -20260811
---

# Exported documents keep their embedded views

A document can embed a live view — a link-preview card grid, a table, any layout
area — by writing a reference to it:

```markdown
@@("Your/Doc/area/OgCard/Edu/Underwriting")
```

On screen that renders as the view. **Exported to PDF or Word, it did not.**

The exported file showed the reference's raw source text, character for
character — `@@("Your/Doc/area/OgCard/Edu/Underwriting")` printed in the middle
of the page where the cards should have been. The pixel-faithful deck export had
the mirror-image problem: it left the area *blank*, printing an empty gap
instead.

Neither failure announced itself. There was no error, no warning in the export
log, and nothing in the file to suggest anything was missing — so a document sent
to a client could be wrong without the sender ever seeing it. Any document with
an embedded view has been affected since embedding was introduced.

**Exports now render the view.** The platform reads the view's real content when
it builds the file, and writes it in as genuine document structure: a card grid
becomes a real table, with real cells, in both PDF and Word. It stays selectable,
searchable and editable — not a screenshot of a table, and not text pretending to
be one. Links inside a card keep working, resolved against your portal's address
so they still open after the file has been mailed on.

## When a view cannot be rendered

Sometimes it genuinely cannot: the view may be one you do not have permission to
read, or it may no longer exist. Silently leaving a gap would be the worst
outcome — the file would look complete while missing a section, and neither you
nor the recipient could tell.

So an unavailable view now leaves a short, visible note naming it and pointing
back to the portal, in your own language. One unavailable view never fails the
export: the rest of the document renders normally.
