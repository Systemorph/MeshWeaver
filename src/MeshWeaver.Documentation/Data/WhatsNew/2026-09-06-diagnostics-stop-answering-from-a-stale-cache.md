---
Name: Diagnostics stop answering from a stale cache
Category: Fix
Description: The language service reused a NodeType's compiled workspace whenever its sources were unchanged — even when its reference set had moved — so diagnostics could report errors that no longer existed, or miss ones that did.
Icon: DocumentError
Order: -20260906
---

`get_diagnostics` could report an `Error` for a NodeType whose own compilation status was `Ok`, and
keep reporting it: on one portal the same 403,625-byte payload came back byte-identical nine minutes
apart.

The workspace cache treated a NodeType's **source versions** as the whole identity of its
compilation. But the reference set moves independently of the sources — a module arriving or
changing alters what the code compiles against without touching a single source file. When that
happened, the cached workspace stayed pinned to the old references and answered from them
indefinitely, because nothing else could evict it.

The cache now keys on everything a workspace is built from: the reference set and its order, the
skeleton, the global usings and the assembly name, alongside the source versions it already tracked.
Unchanged inputs still reuse the workspace, so editing stays as fast as before.

The direction that mattered most is the quiet one: the same staleness could answer *clean* for a type
that had since broken, and this tool is the one the pre-deploy sweep relies on.
