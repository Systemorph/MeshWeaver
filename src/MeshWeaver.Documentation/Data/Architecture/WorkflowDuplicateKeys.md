---
Name: Duplicate Keys in Workflow YAML
Category: Architecture
Description: A duplicate mapping key in a workflow file is accepted silently and the LAST one wins — so a pin can move in the diff and not in the job. The near-miss that opened the class, why every existing gate was blind to it, and the guard that names the file, the key and both lines at the first job.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/><path d="m12 12 5 5"/><path d="m17 12-5 5"/></svg>
---

# Duplicate Keys in Workflow YAML

**A YAML mapping that writes the same key twice is not an error. The loader keeps the LAST value
and says nothing.** `yaml.safe_load` does it, `yaml.load` does it, and every guard in this fleet
parses workflows through one of them — so a workflow carrying a duplicate key parses, passes every
shape check, runs the job, and uses a value the diff never shows.

```yaml
  validate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@<NEW>
    with:
      platform-ref: <NEW>
      centralized-gen-manifests: true
    with:                                   # ← silently wins
      platform-ref: <OLD>
```

The diff looks like it moved a pin. The job kept the old one.

## The near-miss that opened the class

Measured **2026-09-07**, in the satellite wave adopting
[#3560's](/Doc/Architecture/ModuleBuildArchitecture) `centralized-gen-manifests` input. Each
caller's `validate:` job needed a `with:` block. The patcher located the job's end with
`s.index("\n  ", i)` — a pattern that also matches `"\n    with:"`, because that string starts with
a newline and two spaces. So it cut the block after `uses:` and **appended a second `with:`**, in
MeshWeaver.Education and MeshWeaver.Crm.

`yaml.safe_load` accepted it. The `uses:` sha had moved to the new lane and the effective
`platform-ref` had not, so the lane would have fetched its central guard scripts — *and the
canonical `gen-manifests.py`* — at the **old** ref while running the **new** lane. It was caught
before pushing by reading the rendered block, not by any gate.

## Why every gate was blind

[Pin Set Consistency](../PinSetConsistency) (`Platform pins name one build`) models the
*consequence* — a `uses:` sha and a `platform-ref` that disagree — and does red on it. Its report on
a real instance of the *outcome*, MeshWeaver.Crm#62, run `34083378598`:

> `MeshWeaver.Crm — ci.yml:362 calls .github/workflows/node-repo-validate.yml@c7fef7a2… but passes platform-ref 0a2b9017… (line 367).`

🚨 **That PR is a different mechanism with the same signature.** Crm#62 is a Dependabot bump whose
whole diff is `+1/-1` on `ci.yml`: it moved the `uses:` line and left `platform-ref` alone. No
duplicate key involved. It is evidence that this *class of stale-pin outcome reaches main's queue
routinely* — not evidence that anything detects duplicate keys.

| | Covered before #3579 |
|---|---|
| a duplicate key whose effect is a **pin mismatch** | only where `Platform pins name one build` is a **required** context — measured 2026-09-07: MeshWeaver.SocialMedia and MeshWeaver.Crm **yes**; MeshWeaver.Reinsurance and MeshWeaver.Education **no** |
| a duplicate key with **any other effect** — a duplicated `if:`, `env:`, `needs:`, `timeout-minutes:` | **nothing, anywhere** |
| *when* it is reported | after a full CI run, not at the first job |

## The guard

`.github/scripts/check-workflow-yaml-keys.py`, beside
[the 45-minute cap](../NodeRepoGateSharding) and
[the PR-secret preflight](../DependabotSecretStore). It reads no secret, calls nothing, and takes
milliseconds — so it runs on forks and on Dependabot pull requests, at the first job.

**The rule.** For every workflow YAML under `.github/workflows/`, and every composite-action
`action.yml` / `action.yaml` in the tree, no mapping may contain the same key twice. Two keys are
the same when

- they are **written identically** (`with` and `with`), or
- they are written differently but **resolve to the same key**. `on:`, `yes:` and `true:` are all
  the boolean `True` under PyYAML's YAML-1.1 resolver, so `safe_load` merges them while GitHub's
  YAML-1.2 parser keeps them apart. A disagreement between what the gates see and what the runner
  sees is exactly the hazard this file exists to remove, so it is reported rather than tolerated.

Composite actions are scanned **when present** and are never required to exist: a repo with no
composite action is not a repo with a missing gate. A repo with no *workflows* directory, on the
other hand, fails — [nothing to gate is a failure, not a pass](../ReadingCiSignals).

### The verdict names the file, the mapping, the key and BOTH lines

The near-miss must be reproducible from the message alone, so the message carries the shadowed line
as well as the shadowing one:

```text
::error file=.github/workflows/ci.yml,line=10::duplicate key `with` in mapping `jobs.validate` —
first written at line 7, shadowed by the one at line 10. YAML takes the LAST, so everything under
line 7 is dead; `yaml.safe_load` accepts this silently, so the file parses, the shape gates pass
and the job runs with the value the diff does not show (#3579)
```

### Anchors, aliases and merge keys — a decision, proven rather than claimed

The scan walks the composed **node graph** (`yaml.compose_all`), never a constructed dict. That is
what makes the following three answers possible at all, and each is a case in `--self-test`:

| Shape | Verdict | Why |
|---|---|---|
| `&base` … `*base` — an anchor and its aliases | **silent** | legitimate YAML. An aliased mapping is the *same node object* at every use site, so it is checked once and never double-reported (which also makes a recursive anchor terminate instead of blowing the stack) |
| `<<: *base` where the merged mapping supplies a key the local mapping also writes | **silent** | YAML defines the explicit key as the winner. That is a documented override, not a shadowed value, and redding it would be redding correct YAML |
| `<<: [*a, *b]` | **silent** | the spec's own way to merge several mappings: one key, a sequence value |
| two separate `<<:` keys in one mapping | **fires** | a literal duplicate key with no defined meaning — the loader keeps only the last |

GitHub Actions does not expand anchors in workflow files, so none of this appears in the fleet
today. It is in the self-test so the decision is a measurement rather than a claim, and so a future
composite action using them is not redded by surprise.

## Where it runs

**Core, on itself** — `dotnet-test.yml`, in the workflow-gates job, immediately after the
45-minute-cap guard: `--self-test`, then `--root .`.

**Every satellite, centrally** — `node-repo-validate.yml` fetches it from this repository at the
caller's `platform-ref`, self-tests it, and runs it against the **caller's** tree. That is the same
centralization as `compile-check.py` and `gen-manifests.py`, and for the same reason
([Module Build Architecture](../ModuleBuildArchitecture) — *"scripts are centralized: the lane
fetches the platform's copy at the pin; repos keep only allow-files"*). **A repo hand-rolling its
own copy is behind, not different** — the six vendored copies of `gen-manifests.py` had drifted to
five vintages, and each fix landed in one repo while the other five kept the bug.

The fetch carries no trapdoor: `platform-ref` empty is red, a fetch that fails is red naming the
ref, and a body whose first 400 bytes do not name the script is red. There is no fallback to a local
copy and no `continue-on-error` — [a gate never tests its own inputs](../ReadingCiSignals).

## The self-test is the licence to believe the verdict

`--self-test` plants each defect in a temporary tree, asserts the checker **fires**, then asserts it
stays **silent** on the fix — including the two arms that carry the whole point: that the message
names the file, the key `with`, the mapping `jobs.validate` and both line numbers, and that a tree
with no workflows at all *fails* rather than passing vacuously. An unproven gate is no gate.

🚨 **A self-test is necessary and is not the negative control.** It proves the checker's logic; it
does not prove the checker is *wired* to a job that can fail the run. #3579 was therefore landed by
pushing the guard together with a deliberately planted duplicate key in one of core's own workflow
files, watching the job go red in CI with the file and the key named, and only then removing the
plant. Both run URLs are in the pull request. Same posture as
[Negative Controls](../NegativeControls).

## The fleet was clean when this landed

Swept 2026-09-07 over every satellite's `main`, workflows and composite actions, with this guard:

| repo | head | workflow files | verdict |
|---|---|---|---|
| MeshWeaver.Plugins | `a5d08aa25c1a` | 7 | clean |
| MeshWeaver.Education | `58c4062a910d` | 2 | clean |
| MeshWeaver.Reinsurance | `4dd9fadd791d` | 4 | clean |
| MeshWeaver.SocialMedia | `9408df755cd9` | 5 | clean |
| MeshWeaver.Crm | `d9e2768cba6d` | 4 | clean |
| MeshWeaver.Manufacturing | `cfa72caecc37` | 2 | clean |

None of them carries a composite action today. The Education and Crm duplicates from the near-miss
never reached `main`, which is why the guard could be adopted without an allow file — and there is
no allow file, deliberately: a duplicate key has no legitimate form to exempt.

## Related

[Pin Set Consistency](../PinSetConsistency) — the gate that models the *consequence*, and why it is
narrower on three axes · [Reading CI Signals](../ReadingCiSignals) — why a skipped gate and a passed
gate look identical · [Module Build Architecture](../ModuleBuildArchitecture) — scripts are
centralized and fetched at the pin · [CI Content Bake](../CiContentBake) — the shared node-repo
lanes and how a caller pins them · [The Dependabot Secret Store](../DependabotSecretStore) — the
sibling guard in the same fetch block
