---
Name: A guide for standing up a new repository
Category: Feature
Description: A new /new-repo skill documents the shape every MeshWeaver content repository shares — which files to copy, how its checks are wired, and how its content reaches a portal.
Icon: Sparkle
Order: -20260828
---

# A guide for standing up a new repository

MeshWeaver's content — the packages you install from the Store — lives in a handful of separate
repositories, each holding one family of packages. Adding another one used to mean reading five
existing repositories and guessing which of their differences were deliberate.

The new `/new-repo` guide writes that down. It names one repository to copy from and says why,
lists the files a new one needs, and explains how its checks are wired: the shared build steps every
repository calls rather than reinventing, the job that refuses to run when something it needs is
missing instead of quietly skipping, and the regular rebuild that keeps a repository's packages in
step with each new platform release.

It also records where the existing repositories genuinely disagree, and which answer to take —
including two settings that, chosen wrongly, cause a repository's packages to silently stop
reaching portals while every check still shows green.
