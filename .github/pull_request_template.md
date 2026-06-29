<!--
  Merge preconditions for MeshWeaver (enforced by branch protection on `main` — see
  Doc/Architecture/ReleaseStrategy → "Merge preconditions"). A PR is mergeable only when ALL of
  these hold. CI enforces (a) and (b); a reviewer/branch-protection enforces (c) and (d).
-->

## What & why

<!-- One or two sentences: what changes and why. Link the issue/thread if any. -->

## Merge preconditions

- [ ] **(a) Build is green with warnings-as-errors** — the `Build` job runs `dotnet build -warnaserror`; no warnings.
- [ ] **(b) Tests are green** — the full sharded test suite passes (the consolidated *Test Results* check).
- [ ] **(c) Reviewed** — at least one review (AI **or** human — we don't care which).
- [ ] **(d) Review comments dealt with** — every review thread is resolved (a reply and/or a code change). We expect at least one comment or code change associated with the review.

## Notes for reviewers

<!-- Anything that needs attention: risky areas, follow-ups, manual verification done. -->
