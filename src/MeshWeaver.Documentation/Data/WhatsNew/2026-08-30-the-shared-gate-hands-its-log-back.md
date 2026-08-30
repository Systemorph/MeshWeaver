---
Name: The shared gate hands its log back
Category: Feature
Description: A node repository that adopts the platform's shared gate can now keep its own ratchets — the tester's full output is uploaded as a per-commit artifact, so a repo-local check such as Manufacturing's Tests-area ratchet reads the same verdict lines it read from its hand-rolled job.
Icon: DocumentArrowRight
Order: -20260830
---

# The shared gate hands its log back

The platform's shared gate (`node-repo-gate.yml`) runs the tester inside a reusable workflow, and a
reusable workflow can hand back outputs but not files. A repository with its own ratchet over the
tester's verdict lines — Manufacturing's *Tests-area ratchet*, which turns `tests=skipped` into
debt that must be listed or fixed — therefore could not adopt the shared gate without losing that
check, which is exactly how a wholesale adoption silently drops a repo's own guard.

The gate now uploads the tester's complete stdout as a per-commit artifact, always, and fails if
the gate ran but wrote no log. A repository's own job downloads it and runs whatever ratchet it
keeps, reading the same lines it read before. Nothing changes for repositories that keep no such
check.
