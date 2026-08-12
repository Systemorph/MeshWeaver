---
Name: A page renders even when one of its data sets is empty
Category: Fix
Description: A view that asked for a kind of data the page never actually stocks took the whole area down with it, showing a rendering error instead of the page. Now that question simply answers "nothing here" and the rest of the page renders.
Icon: Bug
Order: -20260810
---

# A page renders even when one of its data sets is empty

A page in MeshWeaver is assembled from several questions asked of your data at
once: give me the products, give me the orders, give me the packages. Most pages
ask for several kinds at a time, and it is completely normal for one of them to
have nothing in it.

Asking for a kind of data the page does not stock is supposed to answer
"nothing here" — and every page is written to expect exactly that answer, showing
an empty list and carrying on. For one family of data types it answered with an
error instead, and because that error surfaced while the page was being drawn, it
did not just blank out one list: it replaced the whole page with **"Rendering
failed."** Everything else on it — the parts with data, the parts that had nothing
to do with the missing set — went away too.

The cause was two different address books that disagreed.

Before answering, the page's data workspace checks whether it knows the kind of
data being asked for. That check was generous: if it did not recognise the exact
kind, it accepted a close relative instead — a deliberate convenience elsewhere in
the system, where a specialised record is filed under its general category. But
the step that actually goes and fetches the data is strict, and looks only for the
exact kind. So a request could pass the friendly check and then fail the strict
one, and the mismatch became an error at the worst possible moment: mid-render.

Now both steps ask the same question. A kind of data the page genuinely stocks
resolves exactly as before; one it does not gets the plain "nothing here" the page
already knows how to display. A missing data set costs you an empty list, never
the page.
