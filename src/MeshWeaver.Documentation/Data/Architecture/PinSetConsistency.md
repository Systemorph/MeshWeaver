---
Name: Pin Set Consistency
Category: Architecture
Description: A repository reaches for the platform under several identities written down in several places, and nothing in GitHub Actions relates one copy to another — so a half-moved set merges green. The invariants that make it red, why three of them are NOT what you would first write, and the two arms that enforce them.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v20"/><path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6"/></svg>
---

# Pin Set Consistency

**Every pinned digest existing is not the same as every pinned digest naming the same build**, and
until #3454 only the first question had an answer.

A satellite repository reaches for the platform under several identities that must all come from one
promoted build:

| Identity | What it is | Why the repo needs it |
|---|---|---|
| the **tester** image (`mw-plugin-test`) | where the compile / render / ACR gates run | the gates' own runtime |
| the **portal** image (`memex-portal-ai`) | the reference set every module compiles against | reference set = runtime set (#940, #2693) |
| the **source ref** (`MW_PLATFORM_REF`, `platform-ref:`) | the core commit whose `.github/scripts/*` a shared lane executes | the lane's own code |
| the **lane ref** (`uses: …@<sha>`) | which VERSION of the shared lane runs | the lane's own definition |

Each of these is written down in **more than one place**, because a reusable workflow's `with:`
cannot read the workflow `env:`. There is no way to express "the same pin" in the file, so the pin
is copied — and **nothing in GitHub Actions relates one copy to another**. Move one copy and leave
another and the run is still green: the gates test one platform while the modules compile against a
different one, and the divergence surfaces days later on a portal as a framework mismatch pointing
at nothing.

## What it cost, twice

- **2026-08-31, MeshWeaver.Plugins `80ebad09`** moved `MW_IMAGE_DIGEST` and `MW_PORTAL_IMAGE_DIGEST`
  to `ci.6988` and left BOTH `platform-image-digest:` literals on the previous build.
- **#3344** — a withdrawal that took a line it did not add **dropped a fourth pin, took core CD down
  for an hour, and every check was green**.

## The survey that opened #3454

Measured 2026-09-06 while staging the coordinated platform-pin move for the 3.0.0 wave:

| repo | pin-consistency gate | literal pin sites a move must pass |
|---|---|---|
| MeshWeaver.Plugins | `scripts/check-platform-pins.py` | 14 |
| MeshWeaver.Reinsurance | `scripts/check-platform-pins.py` | 12 |
| **MeshWeaver.SocialMedia** | **none** | 4 |
| **MeshWeaver.Manufacturing** | **none** | 3 |
| **MeshWeaver.Crm** | **none** | 4 |

🚨 **Zero matches in the bottom three is not a gate that skips — it is a gate that does not exist,
so there is nothing to notice.** The fleet's usual failure mode is a *skipped* gate wearing a
passing tick; here there was no job to skip.

## The invariants

`.github/scripts/check-pin-set-consistency.py` in this repository. Arms I1–I6 and I8 are **static**
and need no credential, so a satellite runs them on every pull request; I7 needs the registry.

| | Invariant | The failure it names |
|---|---|---|
| **I1** | **Same name, one value** — every literal site sharing a declaration name carries one value | `platform-image-digest:` is written twice in each of the three repos (the gate lane and the publish-bake lane); editing one is #3344's shape |
| **I2** | **Same image, one digest** — literals resolving to the same ACR repository agree *even under different names* | `MW_PORTAL_IMAGE_DIGEST` vs `platform-image-digest`; I1 alone cannot see this, because each name was internally consistent |
| **I3** | **A lane call is internally coherent** — a `uses: …@<sha>` that passes a literal `platform-ref:` passes its own sha | the lane runs core's scripts AT `platform-ref`; a lane newer than its ref calls a script that does not exist there (SocialMedia run 33073476996: `can't open file …/node-repo-scope.py`) |
| **I4** | **No placeholder vacuity** — a pin-shaped name whose value ATTEMPTS a digest without being one is a failure | `sha256:PLACEHOLDER` reads to a `sha256:[0-9a-f]+` matcher as "no pin here"; `sha256:df19f10a` reads to it as a good one |
| **I5** | **No unclassified pin** — a literal digest whose image cannot be determined is a failure | a pin the extractor cannot place has not been checked, and "not checked" must never be spelled the same way as "consistent" |
| **I6** | **The denominator is printed**, and zero is red under `--require-pins` | Education pins under three names of its own, so a sweep keyed on the two common names reports it clean while verifying none of its pins |
| **I8** | **No orphaned source ref** — a literal `MW_PLATFORM_REF` must be named by at least one lane the repo calls | the value was moved alone, or everything else was |
| **I7** | **One promoted build** *(`--check-tags`)* — every distinct manifest the repo pins shares at least one TAG with every other | a digest carries no tag, so this is the only way to relate two of them |

### Three of these are deliberately weaker than the obvious version

A gate that fires constantly gets bypassed, and one that must be bypassed is worse than none. Each
of these was written narrower on measured evidence:

- **I3 compares a call against ITSELF, never against another call.** Two lanes at two different core
  commits is the *normal* state: SocialMedia's validate / tag-modules / compile-check sit at one sha
  while its gate and publish-bake ride another, and its own comment says why — *"lane DEFINITION
  pins, not this platform pin; they move with the shared-workflow adoption work."* A gate demanding
  one lane sha per repository would red that on day one.

- **I8 is not "`MW_PLATFORM_REF` equals a lane's sha".** The image an `env:` pins and the source a
  lane runs are two different objects; Plugins#1268 moved two of them together and went red, and its
  landed fix splits them again. What I8 refuses is the value being **orphaned** — declared, and named
  by nothing else in the repository.

- **I4 is narrow on purpose**: the value *attempts* to be a digest (it starts with `sha256`) and is
  not one. Not "any pin-shaped name whose value is not a digest" — the text scan reads `run:` shell
  and nested mappings too, where `digest: $D` and a bare `digest:` with a block under it are both
  ordinary. Flagging those would put a false red on a nightly sweep.

### And one thing it deliberately never compares

The **image gap** — that a satellite's images are built from core's tip while its `MW_PLATFORM_REF`
is pinned — is the *normal and intended* state, closed deliberately (MeshWeaver#1067). Bounding that
gap is `check-platform-pins.py --check-image-gap`'s job in the repositories that have it. This gate
reads it not at all.

## I7 works without the repository naming its build

Four of the six pinning repositories declare no `MW_PLATFORM_SET`. So the tag arm does not ask
*"does `<image>:<set>` resolve to this digest"* — it reads each pinned manifest's tags and requires
the repository's pins to **share** one. Measured 2026-09-06:

```
mw-plugin-test @sha256:642f686f…  tags: 3.0.0-ci.7917, cb675ec, staging-cb675ec-34048513298
memex-portal-ai@sha256:31aab07d…  tags: 3.0.0-ci.7917, cb675ec, cb675ec-p1098103,
                                        staging-cb675ec-34048513298
                    shared by all: 3.0.0-ci.7917, cb675ec, staging-cb675ec-34048513298
```

A half-moved set shares nothing. Where a set NAME *is* declared it must additionally be among the
shared tags, which turns that name from a comment into an assertion — the pin comments had already
drifted once, one of them naming `ci.6775` while the pin was `ci.7227`.

Two answers are refused rather than smoothed: a manifest whose tags cannot be READ is
**indeterminate**, and a manifest carrying **no tag at all** belongs to no promoted build. Neither is
a pass.

## Where each arm runs

| | Static (I1–I6, I8) | Tag arm (I7) |
|---|---|---|
| **Each satellite's own CI** | every pull request, no credential, blocking | — |
| **`pinned-digests.yml` in this repo** | fleet-wide, discovered from the App installation | daily, against the live registry |
| **`dotnet-test.yml` in this repo** | `--self-test` on every core PR — an unproven gate is no gate | same self-test, with an injected resolver |

The satellite lane fetches the script from this repository **at its pinned platform ref** — the same
centralization as `check-workflow-timeouts.py` and `check-pr-secret-preflight.py`
([Module Build Architecture](../ModuleBuildArchitecture): scripts are centralized, repos keep only
allow-files). There is no fallback: a ref the script cannot be fetched at fails red naming it.

## What the satellite lane actually looks like, and how the three repos differ

The three repositories #3454 named adopted the gate on 2026-09-06/07 —
MeshWeaver.SocialMedia#141, MeshWeaver.Manufacturing#58, MeshWeaver.Crm#58 — each as one job in
its own `ci.yml`:

```yaml
  pin-set:
    name: Platform pins name one build
    runs-on: ubuntu-latest
    timeout-minutes: 10
    env:
      MW_PIN_GATE_REF: <a core commit carrying the script>
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-python@v7
      - name: Fetch the platform's pin-set gate at the pinned ref   # gh api contents?ref=…, no fallback
      - run: python3 "$RUNNER_TEMP/check-pin-set-consistency.py" --self-test
      - run: python3 "$RUNNER_TEMP/check-pin-set-consistency.py" --root . --require-pins
```

🚨 **`MW_PIN_GATE_REF` is a script-DEFINITION pin, and it is deliberately named so that I8 cannot
see it.** I8's subject is a name matching `platform.?ref` whose value is a 40-character commit; a
guard ref carrying that name would be read as a platform source ref and then demanded to have a
lane pinned at it, which is exactly the coupling the gate refuses to assert elsewhere
(MeshWeaver.Plugins#1268 moved two of them together and went red). The image a repository pins and
the source a guard runs are two different objects. It is also why the gate could not simply be
fetched at each repo's existing `MW_PLATFORM_REF`: that value is older than the script.

### The three are not variations of one shape

Measured 2026-09-06 — every column a fact about the repository, not about the gate:

| | SocialMedia | Manufacturing | Crm |
|---|---|---|---|
| digest pin sites / classified | 3 / 3 | 3 / 3 | 3 / 3 |
| how the **tester** pin is written | `$GITHUB_OUTPUT` step output | `env: MW_IMAGE_DIGEST` | `$GITHUB_OUTPUT` step output |
| `MW_PLATFORM_REF` declared | yes (`1b5350d5`) | **no** | **no** |
| lane passing a literal `platform-ref:` | 1 (module-pack) | **0** | 1 (validate) |
| platform lane calls | 8 | 4 | 6 |
| calls `node-repo-validate.yml` | yes | **no — hand-rolled** | yes |

So **I3 and I8 have nothing to compare in Manufacturing today**, and the printed denominator says
so rather than the verdict quietly reading "consistent". That is the denominator rule doing its
job at repository granularity: *"0 lanes passing a literal platform-ref"* is a measured zero, and a
reader can tell it from an unchecked one.

The tester-pin row is the one that matters most: **two of the three write it into a
`$GITHUB_OUTPUT`**, which is invisible to any scan of `env:` blocks — and it is the shape that hid
a hole inside this gate's own first draft (see the falsification section below).

### 🚨 Hand-rolling `validate` costs a repository every OTHER central guard too

`check-workflow-timeouts.py` and `check-pr-secret-preflight.py` do not run on satellites by
themselves: they run **inside `node-repo-validate.yml`**. MeshWeaver.Manufacturing does not call
that lane — it hand-rolls `validate` and `tag-modules` — so neither guard had ever executed
against it. Measured 2026-09-06, with both siblings at **0 violations** on the same day:

```
::error file=.github/workflows/ci.yml::job 'preflight'   has no timeout-minutes …
::error file=.github/workflows/ci.yml::job 'validate'    has no timeout-minutes …
::error file=.github/workflows/ci.yml::job 'tag-modules' has no timeout-minutes …
check-workflow-timeouts: 6 job(s) checked, 4 reusable-call job(s) exempt, 3 violation(s), cap=45 min
```

GitHub's default job timeout is **360 minutes** against a fleet cap of 45. Capped in
MeshWeaver.Manufacturing#58; the durable fix is adopting the lane. **The transferable rule: a
repository that keeps a hand-rolled copy of a shared lane silently opts out of every guard that
lane grows later, and nothing anywhere reports it** — the same shape as the pin gate that did not
exist.

### The context is new, so nothing is renamed

Adding a job publishes a NEW status context (`Platform pins name one build`) and renames none, so
this adoption does not hit the trap that a reusable-workflow adoption does — where the context
becomes `<caller job> / <name>` and the old required name stays required and never reports again.
MeshWeaver.Manufacturing encodes that rule as data in `scripts/check-ci-invariants.py`
(`EXPECTED_CONTEXTS`), so the branch-protection change a context change implies is a visible edit
rather than a discovery via a blocked PR; that list is edited in the same commit.

## This is not the same question as [Pinned Image Retention](../PinnedImageRetention)

They share a subject and answer different things, and neither implies the other:

- `check-pinned-digests.py` (#3438/#3462) — **does each pinned digest still EXIST?** Org-wide,
  nightly, against ACR.
- `check-pin-set-consistency.py` (#3454) — **do a repository's pins agree with each other, and do
  they name ONE promoted build?**

**Two manifests that both exist satisfy the first completely and are still the whole defect when one
is `ci.7917` and the other `ci.7574`.** The two scripts deliberately share their extraction shapes,
so they can never disagree about what a pin *is*.

## 🚨 The falsification that found a hole in the gate itself

The gate was falsified both ways against the **real** `ci.yml` of all three ungated repositories —
green as-is over a non-zero denominator, red on a half-moved set naming both values. The third case,
a placeholder substituted for the tester pin, is why the exercise matters:

```
MeshWeaver.Manufacturing   placeholder tester pin   RED  ✅
MeshWeaver.SocialMedia     placeholder tester pin   GREEN ❌
MeshWeaver.Crm             placeholder tester pin   GREEN ❌
```

Manufacturing writes its tester pin as an `env:` key; SocialMedia and Crm write theirs into a
`$GITHUB_OUTPUT`. The first draft's step-output pattern required a well-formed
`sha256:[0-9a-f]{64}`, so `image-digest=sha256:PLACEHOLDER` matched **nothing** and read as "no pin
present" — **the exact vacuity trap the gate exists to close, reproduced inside it, in the shape the
gate itself had not covered.**

`check-pinned-digests.py` carried the identical hole, and its docstring promised otherwise: *"a
declaration whose NAME says digest and whose VALUE is neither a well-formed `sha256:<64 hex>` nor a
`${{ … }}` forward is a FAILURE (MALFORMED), never an absence"* — true of the `env:` shape and not of
the step-output shape, which is how **two of the six** pinning repositories write their tester pin.
Both are fixed, and both self-tests now carry the case in **both** shapes.

The lesson generalises past this gate: **falsify against real production input, not only against a
fixture you wrote.** A fixture carries the shapes you already thought of; the fleet carries the one
you did not.

## 🚨 Where the extractor stops — an OMITTED input is invisible

I3 compares LITERALS. A lane call that **omits** `platform-ref:` entirely takes the lane's own
default, which is `main` — a floating ref, and the very thing the lane's documentation warns about
("`main` floats and makes two runs of identical code able to disagree"). There is no literal to
compare, so the gate says nothing.

That is not a hypothetical corner. Measured 2026-09-06: MeshWeaver.SocialMedia's `validate`,
`tag-modules` and `compile-check` calls carry no `with:` block at all, so all three run core's
scripts at whatever `main` is at the instant the step executes, while the `uses:` refs are pinned.
An *explicit* `platform-ref: main` against a pinned lane is **red** here; the same thing written by
omission is **silent**. The asymmetry is real and it is not defended — it is recorded so the next
reader does not mistake a green verdict for a claim about the calls that pass nothing.

Closing it means deciding whether an omitted `platform-ref` is a defect at all, which is a policy
question about the lane's default, not about this gate. Whoever answers it changes the lane's
default or makes the input required; the gate then sees the literal like any other.

## What it never reads

`.github/workflows/*.yml` and nothing else — structurally, not by an exclusion rule anyone has to
remember. MeshWeaver.Plugins' `clients/react/src/i18n/catalog-source.json` holds the **same core
sha** as the platform pins and is **not one**: it is the commit the i18n mirror's drift guard
compares VALUES against. A repo-wide grep-and-replace moves it silently, and a gate that read it
would then demand it move *with* the platform set — the same mistake, with a red build attached.

## Adding a repository, or a new pin shape

- A pin under a name nothing binds and no alias covers is **I5-red**, by design. Bind it in the file
  (`meshweaver.azurecr.io/<repo>@${{ env.<NAME> }}`, or a sibling `<prefix>:` naming the image beside
  `<prefix>-digest:`), or add the name to `ROLE_ALIASES` with the repository that writes it. That is
  the difference between a name-keyed *sweep* — which reports Education clean while checking none of
  its pins — and a name-keyed *fallback* whose miss is a failure.
- A repository that legitimately pins nothing reports a **measured zero** and passes. A repository
  that is *required* to pin runs with `--require-pins`, where zero is red.

## See also

- [Pinned Image Retention](../PinnedImageRetention) — the sibling question, and why frequent
  republishing is what destroys a pin
- [Module Versioning](../ModuleVersioning) — what a pin is, and why pins move as one set
- [Module Build Architecture](../ModuleBuildArchitecture) — one build shape, every repo
- [Keeping the Platform Source Pin Current](../PlatformRefBumpLane) — the mover for the source ref
- [Reading CI Signals](../ReadingCiSignals) — an absent required context counts as satisfied
- [Duplicate Keys in Workflow YAML](../WorkflowDuplicateKeys) — the other way a pin moves in
  the diff and not in the job: a second `with:` the loader accepts silently, which this gate sees
  only when its effect happens to be a pin mismatch
