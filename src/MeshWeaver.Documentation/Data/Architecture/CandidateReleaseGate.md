---
Name: "Candidate release gate — core is verified against every node repo before it ships"
Category: Architecture
Description: "Gate 1 of the Candidate Release Protocol, as wired in CI: a core image is a candidate until every node repo has compiled its NodeTypes against THAT build. Covers the promote/preview decision, the node-repo workflow patch, and the cross-repo token it needs."
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><rect width="24" height="24" rx="4" fill="#0f766e"/><path d="M6 12.5 10 16.5 18 8" stroke="white" stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>
---

# Candidate release gate

> A core image does not get to call itself a release. It is a **candidate** until every node repo
> has compiled its NodeTypes against *that* build. Only a clean closure gets the tag installs
> self-update from; anything else ships as a **preview**, carrying the list of what it broke.

This page is the CI wiring of gate 1 of the Candidate Release Protocol (the states and failure
semantics are specified in `Doc/Architecture/CandidateReleaseProtocol`). It covers what runs, what
promotion means, and the one thing a human still has to do.

## The gap it closes

Core deleted `AddTracking` on `MessageHubConfiguration`. Three NodeTypes in
`MeshWeaver.SocialMedia` still called it. Every gate that existed was green:

| Gate | Why it passed |
|---|---|
| `dotnet build -c Release -warnaserror` | Node source is `<None>` content — the compiler never sees it |
| The full test suite | Same reason; no test compiles in-mesh source |
| The node repo's own *Compile every NodeType (vs core)* | It compiles against a **pinned** core digest that still had the method |
| That job's triggers (`push` / `pull_request` / `workflow_dispatch`) | They fire when the **plugin** changes — never when **core** does |

Production found it instead: `CompileError` → dependents `UpstreamFailed` → `REFUSING READINESS`
→ the instance hub never activates → every request burns the full 60 s activation budget. Hung
pages and failed liveness probes, on all three portals.

🚨 **The pin is not the bug.** A moving `:latest` makes two runs of identical code disagree — the
plugin repos observed exactly that on 2026-08-04 (main green on `sha256:10462f9a`, the same code
red on `sha256:d8895c8a` an hour later), which is why `MW_IMAGE_DIGEST` exists and why it stays.
The bug is that **nothing re-ran a dependent when the thing it is pinned to moved.** This gate is
that missing re-run — it never bumps a pin, it passes a one-run override.

## What runs

```
merge to main ─▶ Build and Test ─▶ CD: build every image to a STAGING tag
                                        │
                                        ├── candidate-gate ── dispatch each node repo's ci.yml
                                        │      with the candidate digest, wait, aggregate
                                        │
                       clean ───────────┴─────────── broken
                         │                             │
                    promote                        preview
              memex-portal-ai:<version>       preview-<sha> on every repo
              (installs roll forward)         + every break named on the
                                                ci-failure issue + CD red
```

`candidate-gate` needs only the `mw-plugin-test` leg, so it runs **concurrently** with the
portal / migration / bake builds. A node-repo CI run is ~13–15 min and so is the bake leg, so the
CD critical path grows by single-digit minutes rather than by a whole node-repo CI.

**The candidate is `mw-plugin-test`, because that image *is* the framework**: the node repos unpack
`/app` out of it for their compile reference set and run the plugin tester from it. Verifying the
same index that `promote` later tags means the thing tested and the thing shipped are the same
bytes by construction. The candidate is identified by **digest**, never by tag.

Each node repo lands in exactly one outcome, and a run is clean only when *failed* and *blocked*
are both empty:

| Outcome | Meaning |
|---|---|
| `compiled` | Its CI went green against the candidate |
| `failed` | Its CI went red; the report names the failing jobs and every diagnostic line |
| `blocked` | Never attempted — not wired, no access, run never appeared, or timed out. **Never a pass.** |

Every repo is dispatched and every repo is waited for; the walk never stops at the first failure.
That is not a nicety — in the incident, `Post`, `Profile` and `PostsHub` carried the identical
broken call and only `Post` was ever reported, so two of the three bugs stayed invisible until the
first was fixed.

## Promote vs preview

Promotion is a single manifest write. `memex-portal-ai:<version>` is the tag
`SelfUpdateHostedService` selects from (`VersionSelect.PickTarget` takes the newest
`^\d+\.\d+\.\d+` tag), so until that PUT lands, no install can see the commit.

A broken closure publishes the same bytes as `preview-<sha>` on every repository. The prefix is
deliberate: `PlatformVersionTag` requires a leading digit, so a `preview-…` tag is invisible to
every self-updater by construction.

> 🚨 Do **not** be tempted by `3.0.0-ci.<n>-preview`. That *is* dotted SemVer, and its
> alphanumeric last label sorts **above** the clean `-ci.<n>` — every install would roll onto the
> preview.

Nothing is rebuilt to recover. The layers are already in ACR, so once the dependent is fixed the
next CD run re-verifies and promotes.

## 🔧 Human setup — the gate is INERT until this is done

The gate dispatches workflows in **other repositories**, which the run's `GITHUB_TOKEN` cannot do.
Until the secret exists, `candidate-gate` passes the candidate through unverified and annotates
every run with a `::warning::` plus a job-summary block saying so. That is a **loud** pass-through,
chosen over a gate that would stop all delivery until someone mints a token.

**Do these in order. Arming first would report every repo as `blocked` and ship nothing.**

### 1. Patch each node repo's `ci.yml`

`Systemorph/MeshWeaver.SocialMedia`, `Systemorph/MeshWeaver.Plugins`,
`Systemorph/MeshWeaver.Reinsurance`. All three declare the same three inputs — a repo that does
not use one simply ignores it. **All three must be declared**, or the dispatch is rejected with
`Unexpected inputs provided` and the repo is reported `blocked`.

```yaml
on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:
    inputs:
      core_image_digest:
        description: "Candidate framework image digest — overrides MW_IMAGE_DIGEST for this run only"
        required: false
      core_ref:
        description: "Candidate core commit — for repos that build the framework from source"
        required: false
        default: main
      candidate_id:
        description: "Correlation id set by the core candidate gate"
        required: false

# The candidate id makes this run findable AND stops an unrelated push to this repo's main from
# cancelling it. Without it, `cancel-in-progress` on a shared group would kill the gate's run and
# core would read that as "blocked".
# Keep the repo's own workflow name as the fallback, so nothing about a normal run changes.
run-name: >-
  ${{ github.event.inputs.candidate_id
      && format('candidate {0}', github.event.inputs.candidate_id) || 'Plugin Catalog CI' }}

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}-${{ github.event.inputs.candidate_id || '' }}
  cancel-in-progress: true
```

Then make the pin a **default**, not a constant. Add **one step at the top of every job that reads
`MW_IMAGE_DIGEST`** (`compile-check` and `test-repos`); every later step keeps reading
`$MW_IMAGE_DIGEST` unchanged:

```yaml
      - name: "Which framework build is this run gating against?"
        run: |
          set -euo pipefail
          # The committed pin is what a PR in THIS repo builds against (gate 2, unchanged). The
          # candidate gate overrides it for ONE run to answer a different question: does this repo
          # still compile against the core build that is about to ship? (gate 1).
          DIGEST="${{ github.event.inputs.core_image_digest || env.MW_IMAGE_DIGEST }}"
          echo "MW_IMAGE_DIGEST=$DIGEST" >> "$GITHUB_ENV"
          echo "gating against mw-plugin-test@$DIGEST"
```

> 🚨 Write it through `$GITHUB_ENV`, **not** as a step-level `env: MW_IMAGE_DIGEST: ${{ … ||
> env.MW_IMAGE_DIGEST }}`. That form defines the very key it reads, and a map that resolves
> against itself has no defined order — it can silently evaluate to empty, which reads as "no
> pin" and pulls whatever `:latest` happens to be. Writing to `$GITHUB_ENV` from a `run:` block
> reads the workflow-level pin unambiguously and logs which build the run used.

`MeshWeaver.SocialMedia` builds the framework from a core checkout instead of unpacking the
image, so its override is the ref:

```yaml
      - name: Check out core framework (Systemorph/MeshWeaver)
        uses: actions/checkout@v4
        with:
          repository: Systemorph/MeshWeaver
          ref: ${{ github.event.inputs.core_ref || 'main' }}
          token: ${{ secrets.MESHWEAVER_REPO_TOKEN }}
          path: MeshWeaver
```

🚨 **While you are there, delete `if: ${{ vars.MW_TEST_IMAGE != '' }}` from
`MeshWeaver.Plugins`'s `compile-check` job.** An unset variable makes the compile gate *skip*, and
a skipped compile gate reports green having compiled nothing — which is precisely how the
2026-07-21 outage shipped. `MeshWeaver.Reinsurance` already removed it; the comment there explains
why.

### 2. Create the dispatch token

Repository secret **`NODE_REPO_DISPATCH_TOKEN`** on `Systemorph/MeshWeaver`. A fine-grained PAT
(or a GitHub App installation token) scoped to exactly the three node repos, with:

| Permission | Why |
|---|---|
| `Actions: read and write` | `POST /actions/workflows/{id}/dispatches`, read run status, download run logs |
| `Contents: read` | Resolve the ref being dispatched |
| `Metadata: read` | Implied by the above |

It needs **nothing** on `Systemorph/MeshWeaver` itself. Note the expiry: an expired token makes
every repo report `blocked`, which correctly stops promotion — loudly, on the `ci-failure` issue.

### 3. Watch one CD run

The first armed run should show three `compiled` rows in the `candidate-gate` summary before
`promote` runs. If a repo shows `blocked`, its `ci.yml` patch has not landed.

## What this does not cover

It verifies that dependents **compile**. A signature that survives with changed semantics passes
this gate — behavioural compatibility is each repo's own tests, which do run here because the gate
dispatches the node repo's whole CI, not only its compile job.

It also only sees the repos listed in `NODE_REPOS` in `main-cd.yml`. A node repo missing from that
list is a repo the candidate is never verified against — the same blind spot in a new shape.

## Related

- [NodeType Compilation & Releases](/Doc/Architecture/NodeTypeCompilation) — the runtime side: why
  a `CompileError` refuses portal readiness, and why a framework bump recompiles everything.
- [Deploying a plugin change](/Doc/Architecture/DeployingPluginChanges) — the mesh-side tail, once
  an image has shipped.
- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — how a promoted tag reaches
  the portals.
- [Deployment](/Doc/Architecture/Deployment) — the deployment router.
