---
Name: A node no longer shows you its type instead of itself
Category: Fix
Description: Just after a package's type was rebuilt, some of its nodes served the type's own definition in place of their content — so pages read as empty and a paid install reported there was nothing to install.
Icon: Sparkle
Order: -20260813
---

# A node no longer shows you its type instead of itself

Every node says what kind of thing it is, and the platform keeps one list of what each kind's
content looks like — so that a page rendering a node knows what shape to expect. Entries in that
list were being written by two different things: by a node of that kind, which knows the right
answer, and by the kind's own definition, which does not. Whichever wrote last won.

When the definition wrote last, every node of that kind started reading back as **a copy of its own
type definition** rather than its content. Nothing looked broken from the outside: the node kept its
name, its address and its version, no error was logged, and the substitution held for as long as the
process ran. Pages simply showed nothing, and anything that asked a node a question about itself got
an answer that belonged to something else.

The visible cost was on the Store. A purchase whose package was in that state read its own
declaration, found a type definition where the list of things to install should have been, and
concluded there was nothing to install — leaving a buyer charged, entitled, marked delivered, and
holding an empty space. It happened only in the moments right after a package's type was rebuilt,
which is why it looked random; a platform update rebuilds every type at once, so the window opened
for everything simultaneously.

Only the nodes themselves describe their content now, and the definition no longer overwrites what
they said. Alongside that, content is never reshaped into a type it says it is not: if the two ever
disagree again, the platform declines to answer and says so, instead of handing back a confident
wrong one.
