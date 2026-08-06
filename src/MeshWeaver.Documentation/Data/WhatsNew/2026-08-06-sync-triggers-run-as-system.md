---
Name: GitHub sync triggers work again on system-owned Spaces
Category: What's New
Description: Check, update and commit on a GitSynced Space no longer demand write permissions nobody holds — your click authorizes the operation, and the platform executes it.
Icon: ArrowSync
---

Triggering a Space's GitHub sync — Check branch, Update to latest, or Sync now — works again for
everyone it should work for. A GitSynced Space is owned by its repository: the platform recently
started enforcing that by removing per-space write grants, and the sync triggers were still trying
to run under your identity, so even a read-only branch check failed with a permissions error.

Now your click authorizes the operation and the platform executes it. Checking the branch or
updating to the latest commit needs only read access to the Space — the repository is the source
of truth, and an update simply brings the Space in line with it. Committing the Space back to its
repository needs edit rights on the Space or a platform administrator, and platform administrators
can trigger any sync operation on any Space. Your GitHub account is still what pushes: commits are
authored with your connected credential, exactly as before.

This applies everywhere syncs are triggered — the Space's GitHub menu and settings tab, and the
`git_hub_sync` MCP tool. If an operation is denied, the page now tells you why instead of showing
a spinner that never resolves.
