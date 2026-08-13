---
Name: Shipped content is kept on disk at its exact commit
Category: Feature
Description: Content that comes from a git repository can now be kept on disk pinned to the exact commit it was synced from, so work that depends on it can be skipped when the commit has not moved.
Icon: Sparkle
Order: -20260813
---

# Shipped content is kept on disk at its exact commit

Content that ships from a git repository already arrives with a commit identity, but nothing kept that
identity on disk — so anything derived from that content had to be recomputed without a cheap way to
ask "has this actually changed?".

A portal can now keep one checkout per repository and, beside it, a narrow checkout per module pinned
to the exact commit it was synced from. Asking for a module at a commit already on disk does no work
at all, and each module's checkout contains only that module rather than a copy of the whole
repository. Superseded commits are removed when the caller that created them says so, rather than
accumulating.

Nothing depends on it yet — this is the foundation for making compilation skip modules whose sources
have not moved. The commit is the version key that makes "unchanged" answerable without inspecting
every file.
