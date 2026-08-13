---
Name: A store that came up empty can now say why
Category: Fix
Description: A portal reading its plugins from a private repository could show a completely empty store, with nothing anywhere reporting a problem. The credential that makes those reads work is now part of a deployment's configuration instead of something applied by hand and silently lost.
Icon: Sparkle
Order: -20260813
---

# A store that came up empty can now say why

A portal whose plugins live in a **private** repository could come up with an entirely empty store —
no courses, no plugins, nothing to install — while every other part of the portal looked perfectly
healthy. Nothing was marked red, no error appeared in any log, and the page simply said there was
nothing to show.

The reason was a missing machine credential. Reading a repository on your behalf uses a dedicated
app identity, quite separate from the one that signs *people* in. When that identity is absent, the
platform still asks — just anonymously. Against a public repository that keeps working, so the gap
stays invisible. Against a private one the answer is "not found", and "not found" and "this store
is empty" look exactly the same from the outside.

The same missing identity also made installing fail halfway. The install would create the new
item's root, then stop at the step that fetches its contents, leaving behind an entry with a name
and nothing inside it — a package that appears installed but has no pages.

Two things change. The credential is now a normal part of a deployment's configuration, so it is
declared once alongside everything else and survives every redeployment; previously it could only
be attached by hand afterwards, which meant the next routine update quietly removed it again. And
because it is declared rather than improvised, a deployment that reads private content is no longer
one upgrade away from silently falling back to anonymous.

This does not change who may see what. A repository nobody granted access to stays inaccessible;
what changes is that a portal entitled to read one stops behaving as though it were empty.
