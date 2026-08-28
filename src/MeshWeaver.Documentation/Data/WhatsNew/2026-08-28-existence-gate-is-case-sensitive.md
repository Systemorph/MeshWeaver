---
Name: The existence-gate example compares mesh paths case-sensitively
Category: Fix
Description: The canonical "listing for existence, then the owner's stream for content" snippet in the CQRS guide matched paths case-insensitively, which let a different node open the gate for a point read that then could not exist; it now uses an ordinal comparison and says why.
Icon: DocumentSearch
Order: -20260828
---

# The existence-gate example compares mesh paths case-sensitively

The CQRS and content-access guide shows how to read a node that may not exist yet: a children
listing answers *whether it is there*, and only then is the owner's stream opened for *what it
says*. The example gate matched the listed path with `StringComparison.OrdinalIgnoreCase`.

Mesh paths are case-sensitive. A case-insensitive match lets a *different* node — one whose path
differs only in case — satisfy the gate, and the point read that follows opens on a path that does
not exist: exactly the `NotFound` the gate exists to rule out. The snippet was copied verbatim into a
plugin and caught in review, so the guide now uses `StringComparison.Ordinal` and explains the
reason inline.
