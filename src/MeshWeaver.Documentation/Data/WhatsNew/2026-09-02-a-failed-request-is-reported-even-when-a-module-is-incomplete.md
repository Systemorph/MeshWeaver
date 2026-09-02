---
Name: A failed request is reported even when a module is incomplete
Category: Fix
Description: A message whose failure could not be reported is now reported — an incomplete package no longer turns "your request failed" into silence, and the hub names the package and the file it is missing.
Icon: Warning
Order: -20260902
---

# A failed request is reported even when a module is incomplete

When a request could not be delivered, the hub tells the sender so — that failure notice is how a
save, a create, or a page knows to show an error instead of waiting forever.

Writing that notice runs the same serializer as every other message, and the serializer runs a
small diagnostic: it looks through the assembly of the type it is serializing for related types
nobody registered, so it can warn about them. When a package shipped without one of its
dependencies, that look-through failed — and its failure escaped and took the failure notice with
it. The sender never heard back. On the affected portals this looked like requests that neither
succeeded nor failed, with a single "breaking error cascade" line in the log as the only trace.

The diagnostic now stays a diagnostic. It reports what it could not load — once per assembly, with
the assembly's name and the dependency it is missing — and the message it was serializing goes out
regardless. The incomplete package is still a defect (and is fixed separately), but it can no
longer silence the report of some other request's failure.
