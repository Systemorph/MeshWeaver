---
Name: A platform image that modules cannot build against is no longer published
Category: Fix
Description: The portal image is what every plugin module compiles against. Two changes on one day left it carrying an assembly a module also provides, and a pair of libraries that disagree about a version — each break appearing in other people's repositories, on work that had nothing to do with it. The release now refuses to publish an image whose contents cannot be built against.
Icon: ShieldTask
Order: -20260905
---

# A platform image that modules cannot build against is no longer published

A plugin module is not compiled against a source tree or a package feed. It is compiled against the
**portal image itself** — the exact assemblies your portal will load. That is what makes a module
you install actually fit the portal you run it on.

It also makes the image's contents a promise to every repository that builds a module against it.
Until now, nothing checked that promise. The only way to find out what the image contained was to
build something against it and watch it fail — and because a module is built in a *different*
repository, the failure appeared to people who had changed nothing, on work unrelated to the cause,
hours after the image was published.

That happened twice in one day, in opposite directions, and both breaks rode the same image:

- **An assembly was in two places at once.** A component that ships as an installable module was
  also compiled into the portal image. Two builds of one thing: every piece of authored code that
  used it would be turned away at load time on every portal, because the copy it was built against
  and the copy the portal has are not the same build.
- **Two libraries in the image disagreed about a version.** The image gained a SQLite client, and
  the code-analysis engine it has always carried expects a newer build of the same low-level SQLite
  library than the client brought. Neither is wrong on its own; together they make the image
  impossible to build against. Every module in every repository stopped compiling, with no change in
  any of them.

## What changes

The release pipeline now inspects the image's own contents before the image is given a version
anyone can install, and refuses to publish one that breaks either promise: nothing the release also
ships as a module may be compiled into the image, and the image's assemblies must agree with each
other well enough to build against.

The second check does not describe the rules and hope they match — it **builds against the image**,
exactly the way a module does, and demands the same silence. If it cannot see the image's contents
at all, it fails and says so, rather than passing quietly.

For you this means a break of this kind now stops the release that caused it, instead of arriving
days later as an unexplained failure in a repository that did nothing wrong.
