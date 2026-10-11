---
Name: The platform updates independently of its packages
Category: Fix
Description: A package that is not available for a new build no longer holds the platform update. The platform rolls to the newest admitted build, and the packages that do not follow it yet are listed as advisories and catch up on their own.
Icon: ArrowSync
Order: -20261011
---

# The platform updates independently of its packages

> _"Again, we want to roll platform independently of packages."_ — maintainer, 2026-10-11

Before this change, an instance held its platform update whenever the release check said a package
was not ready for the new build. Reasons included a missing prebuilt bake, a build whose identity
could not be resolved yet, a module that did not compile against it, or a catalogue that could not
be read. Packages, in turn, hold their own updates until the platform they need is running.

On 2026-10-11 one deployment hit both holds at once. Its platform waited for a content bake of the
new build. Every package update waited for that same new platform. Neither side could move, while
another deployment ran the same build without problems.

Now the platform update always goes ahead:

- **The platform rolls to the newest build its update policy admits.** No package answer holds it,
  including a missing bake, an unresolvable build identity, a package without an artifact, a red
  verification or an unreadable catalogue.
- **Nothing is silent.** Every package that does not follow the new build is named, and so is the
  reason. These advisories appear on the Updates settings tab and in the check's result line. The
  About page says the platform rolls, or has rolled, and lists them. It no longer shows "Update held
  — a package … is not available".
- **Packages catch up on their own.** A package that is not ready keeps its previous generation, or
  compiles when the new build starts. It moves forward through its own update policy once its
  artifacts are published.

The automatic check, the **Apply update now** button, an instance reboot and the release selection
used by continuous delivery all follow the same rule. Any of these routes picks the same build.
