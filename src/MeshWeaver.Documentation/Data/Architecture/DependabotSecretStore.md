---
Name: The Dependabot Secret Store
Category: Architecture
Description: A Dependabot-triggered run reads a SEPARATE secret store, so a preflight that is behaving perfectly fails red naming secrets that are provisioned. Why the answer is provisioning and not an actor exemption — and the single precondition that makes an actor exemption legitimate, which core meets and no satellite does.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="8" width="18" height="12" rx="2"/><path d="M12 8V5"/><circle cx="12" cy="3" r="1.5"/><circle cx="8.5" cy="13" r="1"/><circle cx="15.5" cy="13" r="1"/><path d="M9 17h6"/></svg>
---

# The Dependabot Secret Store

**Every repository has TWO secret stores, and a Dependabot-triggered workflow run can only see the
second one.** Settings → Secrets and variables has an **Actions** tab and a **Dependabot** tab;
they share names but nothing else. A run opened by `dependabot[bot]` resolves `secrets.X` against
the *Dependabot* store, so a secret that exists — visibly, on the Actions tab, used by every other
run of the same workflow — resolves to the empty string.

The tell is one line in the run log, above the first step:

```
Secret source: Dependabot
```

This is not a bug and not a misconfiguration. GitHub puts a Dependabot pull request in the same
trust class as a fork pull request: the diff was composed from a third-party registry's metadata, so
the run gets a read-only `GITHUB_TOKEN` and no Actions secrets. The Dependabot store exists so that
Dependabot's own update jobs can reach private registries; workflow runs it triggers inherit it.

## Why this reads as a workflow bug and is not one

A `preflight` job asserts every externally-provisioned input and fails RED naming what to provision
— the shape AGENTS.md requires, because *a gate that cannot run must never go grey and read as a
pass*. On a Dependabot run it does exactly that:

```
##[error]Required CI inputs are not provisioned, so the heavy gate cannot run.
         A gate that cannot run fails RED — it must never go grey and read as a pass.
##[error]missing: secrets.REGISTRY_PUBLISH_TOKEN — …
```

Everything about that message is correct except the reader's next move. The maintainer opens the
Actions tab, finds `REGISTRY_PUBLISH_TOKEN` sitting there, and concludes the gate is broken. The
error names the right secret and, unless it says so, the *wrong store*.

**So the first fix is textual and costs nothing: every preflight's remediation line must name the
store the run actually read.** `Set them under Settings → Secrets and variables → Actions` is
actively misleading on a Dependabot run.

## The fleet, measured 2026-09-06

`gh api repos/Systemorph/<repo>/actions/secrets` and `.../dependabot/secrets` return **names only**
— never values, from any credential. Both stores, every repo that has a `.github/dependabot.yml`:

| repo | Actions | Dependabot | in Actions only (raw store diff) |
|---|---:|---:|---|
| MeshWeaver | 14 | **0** | all 14 |
| MeshWeaver.Plugins | 12 | 4 | `ACR_PUSH_PASSWORD`, `ACR_PUSH_USERNAME`, `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `MESHWEAVER_REPO_TOKEN`, `PLATFORM_WEBHOOK_SECRET`, `REGISTRY_PUBLISH_TOKEN` |
| MeshWeaver.Reinsurance | 10 | 4 | `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `MESHWEAVER_REPO_TOKEN`, `MW_REGISTRY_KEY`, `PLATFORM_WEBHOOK_SECRET` |
| MeshWeaver.SocialMedia | 11 | 4 | the six above **+** `REGISTRY_PUBLISH_TOKEN` |
| MeshWeaver.Crm | 9 | 4 | `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `MW_REGISTRY_KEY`, `PLATFORM_WEBHOOK_SECRET` |
| MeshWeaver.Manufacturing | 9 | 5 | `ACR_PASSWORD`, `ACR_USERNAME`, `MW_REGISTRY_INSTANCE_KEY`, `PLATFORM_WEBHOOK_SECRET` |
| MeshWeaver.Education | 9 | 2 | `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `MESHWEAVER_APP_ID`, `MESHWEAVER_APP_PRIVATE_KEY`, `MW_REGISTRY_KEY`, `PLATFORM_WEBHOOK_SECRET` |
| Memex | 8 | **0** | all 8 |

**No repository has a secret in the Dependabot store that is absent from Actions.** The mirror is
always a subset — it drifts one way only, which is why nobody notices it drifting.

### The raw diff is not the failure set

Most of those secrets are consumed by push-only lanes (`publish-bake`, `tag-modules`) or by steps
gated on `if: inputs.publish`, and are simply unused on a pull request. What actually reds a
Dependabot PR is the intersection of the raw diff with **the set each repo's `preflight` asserts on
a `pull_request` event** — and preflights here deliberately assert every input the *later lanes*
consume, not only their own, so that a provisioning gap fails in one cheap job instead of twenty
minutes into a bake:

| repo | asserted on a PR and missing from the Dependabot store | status |
|---|---|---|
| MeshWeaver.Manufacturing | `ACR_USERNAME`, `ACR_PASSWORD`, `MW_REGISTRY_INSTANCE_KEY` | **measured red** — run 33357319017, `Required CI inputs` names all three |
| MeshWeaver.SocialMedia | `REGISTRY_PUBLISH_TOKEN` | **measured red** — run 33354469022, `Secret source: Dependabot` in the same log |
| MeshWeaver.Reinsurance | `MW_REGISTRY_KEY`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | **derived** from `main`'s preflight list × the store. Its one measured run (33354473532) predates that list and died one job later at `compile-check` with `compose-sealed-modules.sh: --registry-url needs --registry-key` — the same absent `MW_REGISTRY_KEY`, surfacing late and unrecognisably |
| MeshWeaver.Crm | *none* | **control** — its preflight asserts exactly the four secrets its Dependabot store holds |
| MeshWeaver.Education | *none* | **control** — PR-time preflight wants `MW_REGISTRY_USERNAME` + `MW_REGISTRY_PASSWORD`, both mirrored; its `AZURE_*` assertion lives in the push-only `publish-bake` |
| MeshWeaver.Plugins | *none* — latent | preflight wants `MW_TEST_IMAGE` + `ACR_*`, all mirrored. `REGISTRY_PUBLISH_TOKEN` reaches `modules-floor`/`modules-rest`, whose publish step is `if: inputs.publish` (false on a PR) |
| MeshWeaver | *none today* — **latent, and the store is empty** | `dotnet-test.yml`'s `shared-rules` and `cross-repo-pair` both assert `MESHWEAVER_APP_ID`/`MESHWEAVER_APP_PRIVATE_KEY`. They are exempted on the actor instead — see below |
| Memex | not measured | store empty; no Dependabot PR open to measure against |

Crm and Education are the two controls that make the rest of the table credible: same shared
workflows, same event, green — because their two stores agree.

## Why the answer is provisioning, not an actor exemption

The tempting fix is `if: github.actor != 'dependabot[bot]'` on the heavy gate. AGENTS.md permits an
exemption expressed **on the event** and forbids one expressed as *"the secret is empty"*, and
`github.actor` is an event property, so the shape looks compliant. It is not, for the satellites,
and the reason is worth stating precisely because core does carry that exact `if:`.

**1 · The fork exemption states an impossibility; a Dependabot exemption states a preference.**
A fork PR *cannot* be given org secrets — GitHub withholds them and no maintainer action changes
that, so skipping is the honest signal and the message ("ask a maintainer") is the only actionable
one. A Dependabot run *can* be given them: one `gh secret set --app dependabot` per name. Exempting
it is therefore a decision not to gate, wearing the costume of a structural limit.

**2 · Core's exemption is legitimate for one reason, and the reason is a precondition, not a
precedent.** `dotnet-test.yml` says it plainly:

> 🚨 And this is NOT a skip-trapdoor, for a reason specific to this workflow: it also triggers on
> `merge_group`, where the credential IS available. So an exempted PR still has its shared rule
> blocks checked — on the queue's temporary ref, with the token minted, BEFORE the merge lands.
> **The exemption moves WHERE the gate runs, never WHETHER it runs.**

That is the test. An actor exemption is legitimate **only** where the same gate also runs on
`merge_group` *and* the merge queue is the only path to the default branch.

**Measured 2026-09-06: core meets it and nothing else does.** `Systemorph/MeshWeaver` ruleset
2128472 carries a `merge_queue` rule. Every other repo in the fleet — Plugins, Reinsurance,
SocialMedia, Crm, Manufacturing, Education, Memex — has only `deletion`, `non_fast_forward`,
`copilot_code_review` and (some) `pull_request` / `required_status_checks`; no `merge_queue` rule in
any ruleset and no merge-queue block in classic branch protection. Several satellites *do* carry a
`merge_group:` trigger in `ci.yml`, which is the trap: **the trigger exists, the event never fires.**
Copying core's `if:` into them would move the gate to an event that has no path to `main`.

**3 · And in a satellite the skipped gate would not even look skipped.** The satellites' required
contexts are the gate jobs themselves (`compile-check / …`, `test-repos / …`), not a single
`collect-results`. A required context that never appears counts as **satisfied**
— see [Reading CI Signals](/Doc/Architecture/ReadingCiSignals). The Dependabot PR would go from
"red, honestly" to "green, having proven nothing".

**4 · A Dependabot PR is the case where the gate matters most.** Manufacturing and Crm watch exactly
one ecosystem, `github-actions`. The diff *is* the CI's own plumbing — `actions/download-artifact`
7 → 8 in Manufacturing#40. A gate exempted from the PR that changes the gate's own machinery is the
worst possible place to spend the exemption.

Conclusion: **provision the missing names into the Dependabot store.** For core specifically the
exemption stands, because core has the queue — but core's empty Dependabot store is worth
remembering as the reason those two gates were exempted rather than fixed.

## The rule

> **A secret that any `pull_request`-triggered lane requires is provisioned in BOTH stores.**
> Adding a line to a preflight's `missing` array is a two-store act. There is no gate anywhere that
> can see the second store, so nothing but this rule keeps them in step.

That invisibility is why this recurred. #2249 (2026-08) was the same defect, closed after
provisioning four names into two satellites. Since then Reinsurance added `MW_REGISTRY_KEY` and the
`AZURE_*` triple to its preflight, SocialMedia added `REGISTRY_PUBLISH_TOKEN`, and Manufacturing was
created — each into Actions only, each re-opening the hole, none visible to any review or gate.
#3399 is the second occurrence.

Two smaller things the audit surfaced, worth converging separately: the same credential is called
`MW_REGISTRY_INSTANCE_KEY` in Manufacturing and `MW_REGISTRY_KEY` everywhere else, and core's and
Memex's Dependabot stores are empty rather than partial.

## Auditing it

Two REST reads per repo. Both return names only — a secret **value** is not readable through the
API from any credential, which is also why mirroring cannot be automated by an agent and needs
whoever holds the values.

```bash
gh api repos/Systemorph/MeshWeaver.SocialMedia/actions/secrets    --jq '.total_count, (.secrets[].name)'
gh api repos/Systemorph/MeshWeaver.SocialMedia/dependabot/secrets --jq '.total_count, (.secrets[].name)'
```

Read `total_count` before reading the list: a truncated listing that is summarised as a count is how
a partial mirror reads as a complete one. Then diff against what the repo's preflight asserts:

```bash
gh api repos/Systemorph/MeshWeaver.SocialMedia/contents/.github/workflows/ci.yml --jq '.content' \
  | base64 -d | grep -n 'missing+='
```

And confirm the store a red run actually read, rather than inferring it:

```bash
gh api repos/Systemorph/<repo>/actions/jobs/<job-id>/logs | grep -m1 'Secret source:'
```

## Remediating it

One command per missing name, same value as the Actions secret. This is repository administration
with fleet consequences — it needs the person who holds the values, and it cannot be done from a
pull request:

```bash
gh secret set <NAME> --app dependabot --repo Systemorph/MeshWeaver.<Repo>
```

Then re-run the Dependabot PR's workflow. The positive signal is the preflight's own success line,
not merely a green wall.

## Related

- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why an absent required context counts
  as satisfied, which is what makes an ill-considered exemption dangerous rather than merely lax.
- [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — the other core gate carrying the
  dependabot actor exemption, for the same credential and under the same merge-queue precondition.
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the shared
  `workflow_call` lanes every satellite's `preflight` feeds.
