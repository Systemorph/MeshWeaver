---
Name: A read grant can no longer be inherited from a node's type
Category: Fix
Description: The dormant node-type "public read" flag has been removed from the permission SQL — it granted nothing in practice, and connecting it would have opened paid course content and private conversations.
Icon: ShieldError
Order: -20260808
---

# A read grant can no longer be inherited from a node's type

The database permission check used to begin with a question that had nothing to do with who you
are: *is this node's **type** marked publicly readable?* If it was, the row was returned and the
per-user permission fold was skipped entirely.

That question is gone. Whether you may read something now depends only on the grants that apply to
you and to the path — which is what everyone already assumed.

## Nothing changes for anyone today

The flag was read from a table that no part of the product ever filled in. On every deployment, in
every partition, it was empty — so the check always answered "no" and the per-user fold decided
everything anyway. Removing it is invisible from the outside, and that is the point: the code read
as though a security rule were in force when none was.

## Why it was removed rather than switched on

Roughly two dozen node types declared themselves publicly readable, and the list is the whole
argument: `Thread` and `ThreadMessage` — every conversation anyone has had with an agent —
alongside `Markdown`, `Code` and `Document`, which between them cover most content in a mesh, and
`Course`, `Module`, `Exercise` and `ExerciseAttempt`, which are paid course material and learners'
own submitted work.

Worse than the list was the shape. The check sat *in front of* the permission fold rather than
inside it, so it did not merely add a grant — it stepped over any **denial**. Course and storefront
gating works by denying access below a public landing page, so a type-level "yes" would have
reached straight past every paywall in the system.

## How to make something public

Two mechanisms already do this properly, and both are honoured by the database and by the live
permission evaluator alike:

- **A partition access policy** marked public read — a grant that takes part in the normal
  resolution, so a more specific denial further down still wins.
- **A type-declared gate**, for a type whose instances open a short, named set of surfaces (a
  storefront cover, a course landing page) and nothing else beneath them.
