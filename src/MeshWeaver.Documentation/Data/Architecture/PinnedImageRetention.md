---
Name: Pinned Image Retention
Category: Architecture
Description: Registry retention deletes what CI pins, and republishing frequency is what destroys a pin rather than what protects it — the second clause the purge rule was missing, the guard that names a dead pin before a repo discovers it, and the retention design that stops the deletion.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5"/><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z"/></svg>
---

# Pinned Image Retention

**A container registry's retention policy and a CI pin are two rules about the same bytes, and
nothing was reconciling them.** On 2026-09-05 the `meshweaver` registry's nightly purge deleted the
manifests `MeshWeaver.Education`'s `main` pinned. Every run of that repo's `Disposable-mesh gate`
then died at `manifest unknown` before a single content gate executed — **including `main`'s own
nightly**, so the repository lost its gate and its green baseline in the same instant. Education,
Manufacturing and SocialMedia went down together at 15:29Z (#3438).

## The rule that was there, and the clause it was missing

The registry's older purge task carries its own reasoning, written after the previous incident
(Memex#122, when the whole `memex-website` repository was deleted and the public brand site served
503 for eleven hours):

> The test for adding any repository here: **if this tag were deleted tonight, would anything
> recreate it?** The repositories below are continuously republished. A pinned tag is not.

That rule is correct and it does not cover this failure. `mw-plugin-test` **passes** it: it is
republished many times a day and carries `latest`, `main`, a version tag, a git sha and several
`staging-…` tags at all times. By the recreate test it is a perfectly legitimate purge target.

It broke anyway, because the hazard is not "nothing rebuilds this repository". It is:

> **Something PINS a specific digest of it — and `--keep N` is counted in NEWER BUILDS, so frequent
> republishing is what DESTROYS the pin rather than what protects it.**

Every satellite pins the platform images by digest, and a pin is *designed* to sit still for weeks:
pins move as one set, deliberately and rarely ([Module Versioning](../ModuleVersioning),
[Module Build Architecture](../ModuleBuildArchitecture)). `--ago 7d --keep 10` guarantees that any pin
older than about a week, or displaced by ten newer builds, names bytes that no longer exist. The
retention window and the intended pin lifetime are in direct contradiction.

So the retention rule has two clauses, not one:

1. *(Memex#122)* If nothing would recreate this tag, never age-purge it.
2. *(#3438)* **If anything PINS a digest of this repository, the purge must not delete what is
   pinned** — regardless of age, and regardless of how many newer builds exist.

## 🚨 The triage trap: the purge and the breakage are never adjacent in any log

Carried forward from Memex#122 unchanged, because it applies identically to a CI runner:
**image caches hide the deletion.** A pod keeps serving from its node's image cache and a warm
runner from its own layer cache long after the manifest is gone. The failure surfaces at the next
*fresh* pull — a reschedule, a new runner, a new environment — which can be hours or days later.

Do not look for an `ImagePullBackOff` or a `manifest unknown` near the purge timestamp. There will
not be one. Work from the pin instead: read the digest out of the repository's workflow and ask the
registry whether it still exists.

## The guard — a dead pin is named before a repository discovers it

`.github/scripts/check-pinned-digests.py`, run daily by
`.github/workflows/pinned-digests.yml` at 05:20 UTC — **after** both retention tasks — and again on
any push to `main` that touches either file.

It does not prevent the deletion. It converts *"an entire repository's CI is inexplicably dead,
including its nightly"* into one red naming the repository, the declaration and the digest. That is
worth having whichever retention change lands, because it also catches the next variant.

```
    repositories scanned                  8
    …declaring at least one digest pin    6
    …declaring NO digest pin              2  (Systemorph/MeshWeaver, Systemorph/Memex)
    …whose workflows could not be read    0
    pin DECLARATIONS found                15
    …over how many DISTINCT digests        9
    …declarations that RESOLVE            12
    …declarations that are GONE            3
    …INDETERMINATE (registry not reached)  0
    pin-shaped declarations MALFORMED      0
```

Measured 2026-09-06, mid-release-wave. The three GONE are Education's, and they are `#3438` itself:
`memex-migration@sha256:d81df6cc79ad…`, `memex-portal-ai@sha256:dab2a7b3a7e1…` and
`mw-plugin-test@sha256:df19f10afc1f…`. Note the gap between **15** and **9**: the fleet moves its
pins as one set, so most manifests are pinned from several repositories at once, and the two numbers
must not be collapsed into one — see below.

### What it is built not to do

Each of these is a way a sweep reports a clean fleet having checked nothing, and each has happened
here in some form:

- **Print the denominator.** "0 unresolved" has two causes — every pin is fine, or nothing was
  extracted — and they are indistinguishable from the verdict alone. So the report states repos
  scanned, repos that pin, repos that do **not**, pins found and pins resolved; and a sweep that
  extracted no pins at all fails, because that proves the extractor broke, not that pinning stopped.

- **Count DECLARATIONS and DIGESTS separately, and label which is which.** The fleet moves its pins
  as one set, so a single manifest is routinely pinned from four repositories at once. "12 pins, 9
  resolve" then invites the reading *nine different images are healthy* when it means nine
  declaration **sites**. Both numbers earn their place and they answer different questions:
  declarations are what a pin move has to edit, distinct digests are how many manifests retention
  actually has to keep alive. The failure summary says it too — *N declarations name M manifests
  that no longer exist* — because N reds from one deleted manifest is one retention event and one
  fix, while N from M is a policy failure of a different size.

- **Refuse placeholder vacuity.** A matcher of the shape `sha256:[0-9a-f]+` reads a *non-hex*
  placeholder such as `sha256:PLACEHOLDER` as "no literal pin present" and passes. Here, a
  declaration whose NAME says digest and whose VALUE is neither a well-formed `sha256:` + 64
  lowercase hex nor a `${{ … }}` forward is a **failure**, never an absence.

- **Find pins by SHAPE, not by name.** A check keyed on the two common variable names would report
  four of the six pinning repositories clean while verifying none of their pins. The fleet writes a
  pin three ways and all three are extracted:

  | Shape | Written as | Who writes it this way |
  |---|---|---|
  | a workflow `env:` assignment | `MW_TEST_DIGEST: sha256:…` | Education (`MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST` / `MW_TEST_DIGEST`), Plugins and Reinsurance (`MW_IMAGE_DIGEST` / `MW_PORTAL_IMAGE_DIGEST`), Manufacturing |
  | a `$GITHUB_OUTPUT` step output | `echo "image-digest=sha256:…"` | SocialMedia, Crm — invisible to any scan of `env:` blocks |
  | an inline image reference | `…azurecr.io/mw-plugin-test@sha256:…` | anywhere a job names the image directly |

- **Know where the extractor stops, and treat a fourth shape as a defect.** Those three are what the
  fleet writes *today*, measured — not a proof that no other shape exists. A digest declared under a
  name that does not say `digest` **and** never written beside its `…azurecr.io/<repo>@` reference
  matches neither rule, and its absence would look exactly like a repository that pins nothing. That
  is why the report prints a **per-repository** pin count and not only the fleet total: a repo whose
  count silently drops from three to two is visible, where a single fleet number would not be. A
  satellite adopting a new pin shape must extend `extract()` and the self-test in the same change —
  a pin the sweep cannot see is spelled identically to a repository that has none, which is the very
  confusion this page exists to remove.

- **Discover the fleet, never commit it.** The repository list comes from the App installation
  (`/installation/repositories`), so a repository added to the org is swept the day the App reaches
  it. `MeshWeaver.Crm` pins the same tester digest as three other repos and is absent from
  `.github/shared-rules.json`'s `repos` array — a check keyed on that list would have swept six and
  silently left a seventh out.

- **Never read "cannot reach" as "gone".** `az` exits non-zero both when a manifest is absent and
  when the registry is unreachable or the credential is refused. Only the registry's own
  `manifest unknown` counts as absence; every other answer is INDETERMINATE and fails the run
  naming the `az` output. A control probe runs first, so "every pin in the fleet is gone" can never
  be produced by a broken login.

- **No skip-trapdoor.** `preflight` asserts every input and fails RED naming what to provision; the
  sweep `needs:` it and carries no input-shaped `if:` and no `continue-on-error:`. There is no
  `pull_request` trigger — the live state of a registry is not a property of a pull request, and a
  credentialed PR-reachable job would put both secrets into the Dependabot store's blast radius for
  no gain. The extractor's own falsification runs on every PR instead, as
  `check-pinned-digests.py --self-test` in the workflow-shell lane.

### The sibling check, on the other axis

`Systemorph/Memex`'s `scripts/check-image-pins.py` asserts the same family of invariant for the
**deployment overlays**: every image the chart *derives* from a committed TAG resolves, not just the
one that is written down (Memex#141 — a portal tag that still resolved while its migration twin was
gone left a Job in `ImagePullBackOff` 639 times in 146 minutes, with the portal up the whole time).
That one covers tags in helm overlays; this one covers digests in CI workflows. **It is not wired
into CI** — its header says so — which is a gap on that axis, not this one.

## The retention half — stop the deletion

🚨 **The purge task definitions are CLOUD-ONLY. They are not infrastructure-as-code, and nothing in
any repository describes them.** Measured 2026-09-06 three ways: `az acr task show` returns an
`EncodedTask` with `contextPath: null` (the YAML is stored base64-encoded in Azure, with no source
repository); an org-wide code search for `acr purge` finds only doc pages; and the deployment repo's
tree carries no bicep, terraform or task definition for them. The shared registry itself is
out-of-band — `deploy/aks/infra/main.bicep` treats `meshweaver.azurecr.io` as an existing
`sharedAcrLoginServer` and only creates a registry when that is empty.

Consequence: **changing retention is a live change to shared registry infrastructure that every
deployment depends on, applied by hand, by whoever holds the registry.** It is deliberately not
something CI does.

### What must NOT be the fix

**Raising `--ago` or `--keep` moves the cliff; it does not remove it.** A pin left stable for a
quarter walks off a 30-day window exactly as it walked off a 7-day one, and the failure is then
rarer, later and harder to attribute. The numbers are not the defect — the absence of any relation
between retention and the pin set is.

### The two tasks having diverged is itself the defect

There are two purge tasks and only one of them learned Memex#122:

| Task | Schedule | Filters | Window |
|---|---|---|---|
| `purge-old-ci-releases` | `0 4 * * *` | `memex-migration`, `memex-bake`, `memex-portal-next`, `memex-portal`, `memex-portal-ai` | `--ago 30d --untagged` |
| `purge-old-images` | `0 3 * * *` | `mw-plugin-test`, `memex-migration`, `memex-portal-ai`, `memex-portal-next`, `memex-bake` | `--ago 7d --keep 10 --untagged` |

Read the overlap: four of the five repositories the 30-day task names are already swept an hour
earlier by the strictly more aggressive 7-day one, so for those four **the 30-day task is a no-op** —
everything it would delete was deleted at 03:00. The only repository unique to it is `memex-portal`;
the only one unique to the aggressive task is `mw-plugin-test`, and that is the one that broke.

That is what "diverged" costs: the hardened task's reasoning does not apply to the repositories that
matter, and a maintainer adding a repository has to know which of two lists is the one that governs.
**One task, one filter list, one window** removes the choice by construction.

### The mechanism that protects a pin: lock the manifest

`acr purge` **skips locked manifests by default** — those with `deleteEnabled` or `writeEnabled` set
to `false` — and deleting them requires the explicit `--include-locked` flag, which neither task
passes. So a lock is a hard protection against this exact purge configuration, not a hint.

The self-maintaining shape is:

1. Derive the live pin set from the fleet's workflows — the guard above already does exactly this,
   and it is the same list.
2. Lock every currently-pinned manifest.
3. Report every locked manifest that nothing pins any more, so a pin move releases the old one.

Step 3 is a **report, not an automatic unlock**, and the asymmetry is the reason: a wrong unlock
costs an outage, a stale lock costs one manifest's unshared layers. A lock placed by a person for a
reason the pin scan cannot see must not be silently removed by a scheduled job.

### The exact commands, for the maintainer to run

Read-only first — this is the current definition, and it is worth capturing before changing it:

```bash
az acr task show --registry meshweaver --name purge-old-images \
  --query "step.encodedTaskContent" -o tsv | base64 -d
az acr task show --registry meshweaver --name purge-old-ci-releases \
  --query "step.encodedTaskContent" -o tsv | base64 -d
```

Lock one pinned manifest (idempotent; repeat per pinned digest — the guard's report lists them):

```bash
az acr repository update --name meshweaver \
  --image mw-plugin-test@sha256:<digest> \
  --delete-enabled false --write-enabled false
```

Release one that nothing pins any more:

```bash
az acr repository update --name meshweaver \
  --image mw-plugin-test@sha256:<digest> \
  --delete-enabled true --write-enabled true
```

Collapse the two tasks into one. `az acr task update` replaces the task's inline YAML; write the
union of both filter lists, keep the reasoning **in the task** as the hardened one already does, and
delete the redundant task in the same sitting so there is only ever one list to edit:

```bash
cat > purge.yaml <<'YAML'
version: v1.1.0
# ONE retention task. Two diverged (MeshWeaver#3438): the hardened one's reasoning did not reach
# `mw-plugin-test`, and for four of its five repositories it was a no-op behind a stricter task
# running an hour earlier. A second list is a second place to forget.
#
# Memex#122 — if nothing would recreate a tag, never age-purge it. `memex-website` is DELIBERATELY
# absent for that reason: its only tag is a pinned `v1` nothing rebuilds, and deleting it served the
# public brand site 503 for ~11 hours.
#
# MeshWeaver#3438 — a repository that IS continuously republished still holds PINNED digests, and
# `--keep` is counted in newer builds, so republishing is what destroys a pin. Protection is per
# MANIFEST, not per repository: pinned manifests are locked (delete-enabled false) and `acr purge`
# skips locked manifests unless `--include-locked` is passed. Never pass it here.
steps:
  - cmd: acr purge --filter 'mw-plugin-test:.*' --filter 'memex-migration:.*' --filter 'memex-portal-ai:.*' --filter 'memex-portal-next:.*' --filter 'memex-portal:.*' --filter 'memex-bake:.*' --ago 7d --keep 10 --untagged
    disableWorkingDirectoryOverride: true
    timeout: 3600
YAML

az acr task update --registry meshweaver --name purge-old-images \
  --file purge.yaml --schedule "0 3 * * *"
az acr task delete --registry meshweaver --name purge-old-ci-releases --yes
```

🚨 Confirm the merged task on a **dry run** before trusting it, and confirm that a locked manifest is
in fact skipped rather than reported for deletion:

```bash
az acr task update --registry meshweaver --name purge-old-images --file purge-dryrun.yaml   # same, with --dry-run
az acr task run   --registry meshweaver --name purge-old-images
```

Note what `--ago` measures: a manifest's `lastUpdateTime`, which a re-push refreshes. A digest that
happens to be republished stays young; a digest that is merely *pinned* does not. That asymmetry is
the whole failure in one sentence.

## See also

- [Image Cleanup](../ImageCleanup) — hand-pruning ACR safely, and the live-keeper rule this page extends from deployments to CI pins
- [Module Versioning](../ModuleVersioning) — what a pin is, and why it is supposed to sit still
- [Module Build Architecture](../ModuleBuildArchitecture) — pins move as one set, deliberately and rarely
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — all-or-nothing publication; verify the image, never the tick
- [A Container Registry in Memex](../ContainerRegistryInMemex) — why the boot image stays on ACR
- [Reading CI Signals](../ReadingCiSignals) — what a green wall does and does not attest to
