---
Name: Node type declarations stop posing as their own instances
Category: Fix
Description: The built-in User, Virtual User and Partition declarations no longer show up as results in their own nodeType queries — so the portal's user directory returns people, and a node type whose path is taken now says so instead of quietly binding to the wrong thing.
Icon: PersonQuestionMark
Order: -20260825
---

# Node type declarations stop posing as their own instances

Every kind of thing in the mesh is described by a **declaration** node — the page you land on at
`/User` describes what a user *is*. Three of those declarations were filed under the very type they
describe: the `User` declaration claimed to be a user, `Virtual User` claimed to be a virtual user,
and `Partition` claimed to be a partition.

That made them indistinguishable from the real thing. Anything asking the mesh "give me all the
users" got the declaration back alongside the actual accounts — and then tried to read a person's
email off a page that describes what a person is. The portal's user directory did this on every
refresh, several hundred thousand times over.

Now a declaration declares itself a declaration, the way the `Space`, `Release` and `Build` ones
already did. Listing users returns people. Installing a package no longer tries to write into a
storage area named after the *word* "User".

## When two things want the same name

A related case, from the same family: a package installed at a short name — say `Feedback` — while
the node type it ships lives one level deeper. A page pointing at just `Feedback` used to attach
itself to the package's own page and serve the wrong thing, leaving nothing behind but one
inscrutable line in the log.

The mesh now checks whether what sits at that name is actually a node type declaration. When it is
not, the page says exactly that — naming both the name it wanted and what it found there instead —
so the fix is obvious: point it at the declaration's real path, or move whatever is in the way.
