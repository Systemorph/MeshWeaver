---
Name: Keeping the Platform Source Pin Current
Category: Architecture
Description: A satellite pins WHICH core commit its src/ compiles against, and until 2026-09-06 nothing moved that pin — the image set had an automated mover, the source ref had a lane wired to nothing. The incident, the adoption, and the two repos where the lane has no subject.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="3"/><circle cx="18" cy="18" r="3"/><path d="M6 9v6a3 3 0 0 0 3 3h6"/><path d="M15 5h6"/><path d="M18 2v6"/></svg>
---

# Keeping the Platform Source Pin Current

A satellite repository reaches the platform under **three** identities, and they answer three
different questions:

| Identity | Written as | Answers |
|---|---|---|
| The **source** pin | `MW_PLATFORM_REF` in the repo's `ci.yml` | which core **commit** this repo's `src/` and its compiled modules are built and tested against |
| The **image** set | `MW_PLATFORM_SET` / `MW_IMAGE_DIGEST` / `MW_PORTAL_IMAGE_DIGEST` | which promoted platform **build** the gates run inside, and which `/app` the modules compile against |
| The **lane** ref | `uses: Systemorph/MeshWeaver/.github/workflows/…@<sha>` plus its matching `platform-ref:` input | which **definition** of a shared workflow runs, and which checkout its central scripts come from |

They may legitimately disagree, and each has its own bump procedure. This page is about the first
one — the one that, until 2026-09-06, had no automated mover at all.

## The incident (2026-09-06)

Core [#3408](https://github.com/Systemorph/MeshWeaver/pull/3408) (`c8c7dd327`) fixed a FIFO
ordering defect in `MessageService.OpenGate`. It was written *for* a MeshWeaver.Plugins `main` red —
`ActivationBacklogFifoTest` observing `B, C, A` — and it merged at **13:06:47Z**.

MeshWeaver.Plugins was still failing on that same test at **13:31Z**, because its `MW_PLATFORM_REF`
— the core commit its `portal-hosts` job compiles `src/` against — was `bbcb22f25` (05:43:18Z),
**49 commits before the fix**. The fix existed; the repo that needed it could not reach it.

🚨 **The staleness comment passed the whole time.** `bbcb22f25` was 10.3 hours and 102 commits
behind, comfortably inside the 24 h / 120-commit bounds the pin's own comment describes as healthy.
That is the lesson worth keeping: *"roughly current"* is not *"carries the fix we merged for this
repo's red."* Age bounds catch neglect. They cannot catch a specific commit being needed.

### Why the gap existed

The **image** half of the platform identity already has an automated mover: core's `main-cd`
publishes a signed build fact to the control instance, whose `PlatformPinUpdater` opens the
`MW_IMAGE_DIGEST` bump PRs on the node repos, and `FrameworkReleaseBroadcaster` fans a
`repository_dispatch: meshweaver-framework-released` out to every subscriber.

The **source** half had a lane and no callers. `.github/workflows/node-repo-platform-ref-bump.yml`
has existed since MeshWeaver.SocialMedia's pin sat days behind on 2026-08-18, and its own header
describes this failure mode in advance — *"a module fix can be merged, green, and STILL never reach
the shipped bundle, because the bundle is built against the pin."* Measured across all four
satellites on 2026-09-06: **not one of them called it.**

## The lane, and what it costs to call

The lane polls the upstream's default branch, compares it to the pin written in the caller's
workflow file, and — when they differ — opens **a pull request**. It never pushes to `main`. That is
deliberate and is the whole reason for having a pin: the bump stays a reviewable commit that the
full gate suite runs on, so a platform regression arrives as a red PR on the commit that moved the
pin rather than as a silently-changed build input on whoever pushed next.

### 🚨 The credential is not a detail — it decides whether any CI runs at all

The lane opened its PRs with `${{ github.token }}`. **GitHub raises no `pull_request` event for
anything `GITHUB_TOKEN` creates** — the recursion guard that produced the #2916 outage one level
down, where four auto-merges landed on `main` and started no `push` lane at all.

Applied to a bump PR, that means: no check runs, no required contexts, and — on
MeshWeaver.Plugins, whose `main` requires five contexts — a pull request that can *never* merge.
And it does not present as broken. It presents as *"checks have not started yet"*, indefinitely. A
person watching a growing pile of bump PRs reads a queue in flight; what is actually there is
automation whose only effect is to make the lag harder to see than it was when nobody was bumping.

So the lane now mints a **GitHub App installation token** (`meshweaver-cloud`, the same credential
`auto-arm.yml` and `release.yml` use), checks out with it so the push is the App's, and opens the PR
as the App. There is deliberately **no fallback** to `github.token`:
`ArmedMergeMustTriggerMainsPushLanesGuard` fails core's build if anyone reintroduces one, and the
same guard now covers PR *creation* as well as merging, in both spellings of the token.

The mint carries no `continue-on-error`. `auto-arm.yml` tolerates a failed mint because a missing
grant is a property of the repository and would paint the identical red on every open pull request
— so its assertion moved to `arm-credential.yml`. This lane is already repo-scoped and runs once a
day against nobody's branch, so a red here *is* the actionable statement, once per day, that the pin
is no longer being maintained.

### Caller contract

```yaml
name: Bump the platform pin
on:
  schedule: [{cron: "17 4 * * *"}]
  workflow_dispatch:
jobs:
  bump:
    permissions: {contents: write, pull-requests: write}
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-platform-ref-bump.yml@<sha>
    with:
      workflow-file: .github/workflows/ci.yml
      pin-key: MW_PLATFORM_REF
      upstream-repo: Systemorph/MeshWeaver
    secrets:
      MESHWEAVER_APP_ID: ${{ secrets.MESHWEAVER_APP_ID }}
      MESHWEAVER_APP_PRIVATE_KEY: ${{ secrets.MESHWEAVER_APP_PRIVATE_KEY }}
```

Every input is passed explicitly even where it matches the lane's default: a default read from a
lane the calling repo did not open is a value nobody here has agreed to, and a new *required* input
on a reusable lane is a silent startup failure for any caller that omits it. Never `secrets:
inherit` — that hands the lane every secret the calling repo owns.

### Why `repository_dispatch` is deliberately not in the contract

The lane's original header suggested subscribing to `meshweaver-framework-released` as well. That
was measured before adopting it, and rejected: each dispatch carries a different
`client_payload.sha`, the branch name is a function of the target commit, so **every dispatch opens
its own pull request**. MeshWeaver.SocialMedia received nine of them on 2026-09-03 (14:42, 16:34,
18:10, 19:06, 19:42, 20:43, 21:00, 21:08, 21:20Z) — nine bump PRs, nine full gate suites, eight
superseded within the hour, and nothing in the lane closes a superseded one.

The schedule is the **floor** the lane exists to provide: the pin is never silently days behind.
`workflow_dispatch` is the same-day path when a specific core fix is being chased, and a
hand-written bump PR stays perfectly normal — [#1410](https://github.com/Systemorph/MeshWeaver.Plugins/pull/1410)
is exactly that, and is what actually closed the incident above.

## Where the lane has a subject, and where it does not

Measured on 2026-09-06 across all four satellites:

| Repo | `MW_PLATFORM_REF` | Adopted | Why |
|---|---|---|---|
| MeshWeaver.Plugins | yes (`ci.yml`) | ✅ | `portal-hosts` builds and tests `src/` against a core **checkout** at the pin |
| MeshWeaver.SocialMedia | yes (`ci.yml`) | ✅ | `src/MeshWeaver.Social` compiles against a core checkout at the pin |
| MeshWeaver.Reinsurance | **no** | ✖ | pins images (`MW_PLATFORM_SET`, digests) only; its `platform-ref:` inputs exist to fetch the shared scripts **at the lane's own sha** |
| MeshWeaver.Education | **no** | ✖ | pins images (`MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST` / `MW_TEST_DIGEST`) only; same `platform-ref:` role |

🚨 **Do not "adopt" the lane where its subject is absent.** Two shapes were considered for
Reinsurance and Education and both are defects:

- **`pin-key: MW_PLATFORM_REF`** — the lane's first step fails RED with *"could not read
  `MW_PLATFORM_REF`"* every single day. Loud, but it is noise: the pin it names does not exist by
  design, and a permanent daily red is how a real red stops being read.
- **`pin-key: platform-ref`** — this matches, and is worse. Those literals are pinned to the *lane's
  own sha* on purpose: a reusable lane runs the central scripts from the checkout `platform-ref`
  names, so moving one without the other is the exact failure of MeshWeaver.SocialMedia's run
  33073476996. A workflow_call lane cannot rewrite a `uses:` (GitHub forbids an expression there),
  so it cannot move that pair as a pair.

Those two repos' core-source coupling is `uses:@<sha>` + a matching `platform-ref:`, and the `uses:`
half already has a mover: **Dependabot**, weekly, via the `github-actions` ecosystem in each repo's
`.github/dependabot.yml`. Note the gap that leaves — Dependabot bumps the `uses:` and does **not**
touch the `platform-ref:` literal beside it, so those two drift on every Dependabot bump. That is a
separate defect from this one and is not fixed here.

## What still is not automated, and is not meant to be

**Nothing here bumps `MW_PLATFORM_SET` or an image digest.** The image set is the sealed identity
every satellite must match; moving it is a release decision, made once for the fleet, not a per-repo
schedule. See [The Continuous Delivery Contract](../ContinuousDeliveryContract) and
[Bake Identity Mismatch](../BakeIdentityMismatch).

**The bump PR is still the integration test.** There is no `Dependent suites (MeshWeaver.Plugins)`
check in core: a core PR being green does not mean a dependent still builds, and for a dependent
that *pins* core, even the release-event rebake does not close that gap — the event moves what is
BAKED, not what `src/` compiles against. The pin bump PR is where the dependent's suite meets the
candidate core commit, which is precisely why it must be a PR that CI actually runs on. See
[The Cross-Repo Pair Gate](../CrossRepoPairGate).

**A schedule is a floor, not a latency guarantee.** It answers "is the pin drifting?" It does not
answer "did the fix we merged 25 minutes ago reach the repo we merged it for?" That question is
answered by a person bumping the pin, and — now — by `Hosting/RepoHealth`'s `main-health` check,
which reports a satellite's `main` being red instead of leaving it to be found hours later.
