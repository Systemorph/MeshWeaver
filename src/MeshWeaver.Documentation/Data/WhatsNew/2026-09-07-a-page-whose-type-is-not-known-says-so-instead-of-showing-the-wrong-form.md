---
Name: A page whose type is not known says so instead of showing the wrong form
Category: Fix
Description: When a node's content type could not be resolved, the property panel quietly built its form over the raw JSON wrapper — a page that looked finished and was wrong. It now names the type it could not resolve, and it resolves more types than before.
Icon: Bug
Order: -20260907
---

# A page whose type is not known says so instead of showing the wrong form

Every node page shows a property panel built from the shape of that node's content. Working out
which shape applies takes a name stored with the content and a table of the types the platform
knows. Almost always the two meet and the panel is correct.

When they did not meet, the panel did something worse than fail: it built the form over the raw
JSON wrapper the content was still sitting in, so the page filled with the wrapper's own internal
fields instead of the node's. There was no error, no warning and no gap on the page — just a form
that looked complete and described nothing. If you were looking at a node whose type had not been
compiled yet, or whose type no longer exists, that panel was what you saw.

Two things change.

The panel now asks the mesh-wide table of content types, keyed on the node's own type — a question
that has exactly one right answer, where the previous lookup could only ask by name and had to
refuse any name that two packages both use. So a page that previously showed nothing useful now
often shows the real form.

And when the type genuinely cannot be resolved, the panel says which name it could not resolve,
and which of the two possible reasons applies: a type compiled inside the mesh may simply not have
finished compiling, in which case the page updates itself when it does and there is nothing to do;
otherwise nothing declares that name and the content needs repair. Content stored as free-form JSON
— which is allowed — gets its own, different note, because there is nothing to wait for there.
