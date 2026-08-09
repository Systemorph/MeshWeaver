---
Name: Why a green build does not mean your node types still compile
Category: Feature
Description: New documentation explains that code stored in mesh nodes is compiled by the portal at runtime rather than by the build, and gives the check to run before shipping.
Icon: Sparkle
Order: -20260809
---

# Why a green build does not mean your node types still compile

Code that lives in a mesh node — a node type's source, a script, a layout area — is compiled by the
portal when it runs, not by the build that produces the portal. That makes it possible for
everything to look healthy right up until a deployment: the build passes, the tests pass, the change
is reviewed and shipped, and only then does a node type turn out not to compile any more.

It also matters that a node keeps its own history. The copy stored in your mesh can be older than
the file it was originally created from, so checking that file is not the same as checking what the
portal will actually build.

Because every node type is rebuilt whenever the platform itself is updated, problems of this kind do
not appear one at a time. They accumulate quietly and then all surface together on a single update,
where a node type that fails to build can hold back the whole portal.

The documentation now spells this out, along with the check to run before shipping: list every node
type, read its diagnostics, and fix the ones reporting problems — including warnings, since an
unregistered content type is what makes a page render empty instead of showing an error.
