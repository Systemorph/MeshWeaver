---
Name: OwnPrArtifacts
Category: Architecture
Description: The named-artifact transport for PRs on our runners, its rollout gates, and the distinction between queue admission and build execution
Icon: CloudArchive
---

# PR artifacts on our infrastructure

The PR remains a GitHub review and check surface. Moving its compute to ARC does not move its
artifact bytes: a workflow using `actions/upload-artifact` still writes to GitHub. A module's
durable object-store copy alone does not solve this either; classifiers, build outputs, receipts,
test logs and cross-run publication attestations also cross job boundaries.

## Named-artifact transport

The shared actions `.github/actions/upload-artifact` and `.github/actions/download-artifact`
accept an explicit `store` input. Empty or `gha` retains the GitHub actions' behavior, including
public core and fork PRs. `file:/ci-artifacts` selects the stdlib adapter
`.github/scripts/ci-run-artifacts.py`. A declared store that cannot be used is an error, never a
fallback to GitHub. The named adapter currently supports `file:` only; the lower-level object's
`azblob:` support does not imply that named listing and extraction support it.

Each artifact has a content-addressed archive and an atomically published manifest containing
repository, run, attempt, name, digest, inventory and expiry. Independent names have independent
manifests, so parallel jobs do not overwrite a shared index. An attempt cannot replace different
bytes under the same name unless `overwrite: true` is explicit. Download checks digest, inventory
and safe paths before extraction, rejects links and preserves executable file permissions.

Failed-job reruns do not rerun successful producers. A same-run download therefore selects the
latest published artifact attempt no newer than the consuming attempt. A download from another
run selects that run's latest attempt, not the caller's unrelated attempt number. No selection
crosses repository or run boundaries. Retention is recorded per artifact; a pruner must honor it
instead of deleting all run inputs when a run finishes.

Own-store uploads have no GitHub numeric artifact ID or download URL. The `artifact-locator`
output is the own-store identity; `artifact-id` and `artifact-url` are empty. Code using GitHub's
artifact REST endpoints or `gh run download` must be migrated along with workflow steps.

## Rollout gates, not assumptions

The transport being present is not proof that a private PR uses it. Rollout requires all of:

1. Both runner sets mount the **same backing share**. Two dynamic PVCs with the same name in
   different namespaces create two independent stores. Read/write proof must cross from a plain
   runner to a Docker-capable runner and back, with no GitHub artifact handoff.
2. Every producer and consumer in the PR graph uses the declared store, including cross-run
   publication lookup and module reuse. A missing historical own-store baseline causes a full
   rebuild; it does not authorize falling back to GitHub storage.
3. The retention controller covers named manifests and immutable archives. Verify cleanup as
   well as upload; moving unbounded growth to our share is not an implementation of retention.
4. A real PR completes its required checks with zero new GitHub artifact uploads. Existing
   GitHub artifacts are separate historical data, not evidence of a new upload.

The actions are introduced before callers reference them at `@main`, because the runner resolves
remote actions before evaluating their step conditions. Referencing an action not yet on `main`
would break even the jobs whose own-store branch was meant to be skipped.

## Queue admission is not a native worker

The existing `Hosting/Build` scheduler admits and orders builds and posts `MeshWeaver/admission`.
GitHub Actions still executes the intermediate job graph on our ARC workers. The planned native
build-job claim/worker and per-stage callbacks are a separate deliverable. Do not describe enabling
queue admission or migrating artifacts as having implemented that worker.

Measured during the 2026-09-17 investigation: Plugins' `MW_BUILD_QUEUE` was `off`; the build portal
reported no free space on `/data`, with module staging and set-record writes failing. Those facts
must be rechecked before enabling admission. A failed check-post must not be recorded as successful
admission, and a full build portal must not be treated as a healthy queue merely because its pod
is Ready.

See [CI artifact storage](CiArtifactStorage.md) for the dated billing evidence and the lower-level
object-store design. Deployment-specific shared-volume bindings belong in Systemorph/Memex.
