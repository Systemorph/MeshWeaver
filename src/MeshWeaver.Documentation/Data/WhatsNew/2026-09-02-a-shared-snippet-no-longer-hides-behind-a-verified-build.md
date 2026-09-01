---
Name: A shared snippet no longer hides behind a verified build
Category: Fix
Description: Code pulled in with an @@ include is part of what a type is built from, so editing it now changes the build's fingerprint — a prebuilt version made before that edit is no longer reported as verified against source it was never built from.
Icon: ShieldCheckmark
Order: -20260902
---

# A shared snippet no longer hides behind a verified build

A type's source is not only the files under its own `Source/` folder. A file can pull another node's
code in with an `@@` line — the way shared helpers, sample data and cross-package snippets are
reused — and that code is compiled into the type just as much as the file that referenced it.

The platform recently learned to *prove* that an installed prebuilt version of a type was built from
the source this portal actually holds, and to mark it **verified** when it is. That proof was reading
a smaller set of files than the compiler was. An `@@` target is, by definition, a node that none of
the type's source queries match — so it was inside the compiled code and outside the check. Edit a
shared snippet, and nothing moved: the portal went on reporting a build made *before* your edit as
verified against the source it now holds.

That is worse than not checking at all. "We don't know where these bytes came from" is a warning
anyone reads correctly. "Verified" is a statement — and a statement about source nobody looked at is
the same failure the check exists to prevent, one step further in.

**Both halves now follow the `@@` lines before they hash.** The build already expands them, so it
simply keeps the list of what it pulled in; the portal resolves them against its own content. Nested
includes count too — a snippet that includes a snippet is followed all the way down — and a
circular reference stops rather than spinning. Edit a shared snippet today and the fingerprints
diverge, so a prebuilt version made before the edit is refused and rebuilt from what you actually
have.

Two things were deliberately kept as they were, because the opposite mistake is an outage:

- **Nothing new is refused on a doubt.** If the portal cannot *read* an included snippet — a slow
  moment, an owner that did not answer — that is "I could not check", not "the snippet is gone". A
  shortened list would hash differently from every honest package and reject good ones, so instead
  the previous answer stands and the build is reported as unverified rather than wrongly refused.
  Packages published before any of this existed still install exactly as before.
- **A snippet edit still does not, by itself, queue a rebuild.** Deciding *when* to recompile is a
  separate mechanism watching the type's own files, and it works the same way it always has. What
  changed is the honesty of the claim: the portal no longer says "verified" about a build it has
  not actually checked.
