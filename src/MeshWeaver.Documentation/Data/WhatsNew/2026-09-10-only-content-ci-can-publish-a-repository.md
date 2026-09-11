---
Name: Only content CI can publish a repository
Category: Fix
Description: A green probe, deployment check or pull-request updater can no longer make GitSync import a repository whose actual content build is red.
Icon: Checkmark
Order: -20260910
---

GitSync now accepts a build completion only from the repository's content-CI workflow. The workflow
trigger and branch are still checked, but they can no longer substitute for evidence about what the
workflow compiled. This prevents unrelated green workflows from publishing a commit while its real
content build is failing.
