---
Name: A search that did not run now says so
Category: Fix
Description: Content search answered "count 0, no results" when the deployment had no search index at all — the same answer as a real empty result. The envelope now carries no count when nothing was searched, so "we did not look" can no longer be read as "there is nothing there".
Icon: SearchInfo
Order: -20260830
---

# A search that did not run now says so

Content chunk search is only available on a deployment configured with a vector store and an
embedding provider. Where it is not, the search machinery is deliberately switched off rather than
throwing — a capability that is off should say so, not send the caller hunting a data bug.

It said so in a `message`. But it also answered:

```json
{ "count": 0, "results": [] }
```

which is exactly what a search that ran and matched nothing answers. And `count` is the field
callers actually read.

That mattered because of what the search is used for. Before any public framework surface is
removed, the procedure is to sweep the live mesh for callers — content that compiles inside the
portal at runtime and that no compiler can see. On a deployment without an embedding provider that
sweep returned `count: 0`, and `count: 0` reads as *"no callers, safe to delete"*. A verification
step that cannot fail is not a verification step.

An envelope for a search that never ran now carries **no `count` and no `results` at all**:

```json
{ "searched": false, "error": "search-not-performed", "message": "…what to configure…" }
```

Omitting the fields is the point. A caller testing `count == 0` now finds nothing there, instead of
finding a zero that means the opposite of what it looks like. The same applies to the other ways a
search never starts — no query text, no scope to search — which were reporting a zero for the same
non-reason.

A search that genuinely ran still reports its count, including a genuine zero, and that is asserted
too: "omit the count" must never quietly become "omit the count when it is zero", which would hide a
real empty result.
