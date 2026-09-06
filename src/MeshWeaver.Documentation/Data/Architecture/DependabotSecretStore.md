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

### The raw diff is not the failure set — and neither is the preflight list

Most of those secrets are consumed by push-only lanes (`publish-bake`, `tag-modules`) or by steps
gated on `if: inputs.publish`, and are simply unused on a pull request. So the raw diff over-counts.

The first attempt at narrowing it intersected the raw diff with **the set each repo's `preflight`
asserts**, and that denominator is wrong — it is the very defect this page documents, one level up.
A preflight's list is hand-maintained and goes stale; a secret can be *consumed* by a pull-request
job that no preflight ever asked about, and then the empty value surfaces deep in a later lane
wearing a message that names no secret at all. Measured on MeshWeaver.Reinsurance#128 (run
33354473532): `Required CI inputs` **passed**, and `compile-check` died one job later with

```
##[error]compose-sealed-modules.sh: --registry-url needs --registry-key
```

— an empty `secrets.MW_REGISTRY_KEY`. Using the preflight list as the denominator would have scored
that run as *no gap*.

The correct denominator is **what a pull-request-reachable job actually references**: every
`secrets.NAME` in a job whose `if:` does not exclude a Dependabot pull request, including names the
caller passes into a shared `workflow_call` lane. Measured that way on 2026-09-06 with
`check-pr-secret-preflight.py --check-stores` (see below):

| repo | consumed on a Dependabot PR and missing from the Dependabot store | status |
|---|---|---|
| MeshWeaver.Manufacturing | `ACR_USERNAME`, `ACR_PASSWORD`, `MW_REGISTRY_INSTANCE_KEY` | **measured red** — run 33357319017, `Required CI inputs` names all three |
| MeshWeaver.SocialMedia | `REGISTRY_PUBLISH_TOKEN`, `MW_REGISTRY_KEY` | `REGISTRY_PUBLISH_TOKEN` **measured red** (run 33354469022); `MW_REGISTRY_KEY` reaches `compile-check` and `test-repos` and no preflight asked for it |
| MeshWeaver.Reinsurance | `MW_REGISTRY_KEY` | **measured red** at `compile-check` (above). Its `AZURE_*` triple was mirrored on 2026-09-06 |
| MeshWeaver.Crm | `MW_REGISTRY_KEY` | **latent, not a control** — same `compile-check` / `test-repos` path as Reinsurance, same absent key. Its preflight does not assert it, which is why it looked clean |
| MeshWeaver.Education | `MW_REGISTRY_KEY` | **latent** — the `e2e-*` jobs consume it whenever `changes.outputs.mesh` is true; its preflight asserts only the registry user/password |
| MeshWeaver.Plugins | `REGISTRY_PUBLISH_TOKEN` | **latent** — passed into `modules-floor`/`modules-rest`, whose publish step is `if: inputs.publish` (false on a PR), so it is unused today and its emptiness is not yet fatal |
| MeshWeaver | `MESHWEAVER_APP_PRIVATE_KEY` | **latent** — `auto-arm` and `merge-queue-steward` both run on a Dependabot PR and both assert the App credential. `dotnet-test.yml`'s two credentialed gates are actor-exempted instead (below) |
| Memex | `MESHWEAVER_APP_PRIVATE_KEY` | **latent** — its `auto-arm` calls core's shared lane with the same pair |

**There are no controls.** Under the correct denominator every repository in the fleet has or had a
gap; Crm and Education only looked clean because their preflights were the *least* complete, which
is the failure mode inverting the signal.

### Two structural facts that shape any fix

**There is no Dependabot *variables* store.** `GET /repos/{owner}/{repo}/dependabot/variables`
answers 404; a Dependabot run reads ordinary repository variables. So `vars.X` is single-store and
**only `secrets.X` is doubled** — which is why `vars.MW_TEST_IMAGE` resolves perfectly in the same
run whose `secrets.ACR_USERNAME` is empty, and why a reader comparing the two concludes the
workflow is broken. When a name has to be duplicated, check which namespace reads it.

**No CI credential can read the Dependabot store.** The `permissions:` block has no `secrets` or
`dependabot-secrets` key, so `GITHUB_TOKEN` cannot list either store; and the only GitHub App
installed on the org (`meshweaver-cloud`, app id 4220566) holds `contents` / `metadata` /
`pull_requests` only. A workflow that diffs the two stores therefore cannot exist today without a
new credential.

That matters less than it sounds, because **a name diff is the weaker instrument anyway**: no API
returns a value, so a secret present with an EMPTY value is indistinguishable from a healthy one by
name — and an empty value is exactly the failure mode this fleet has hit. The assertion that does
catch it is the in-run one, `[ -n "${X:-}" ]`, which tests emptiness in whichever store *this event*
resolves against. That is why the enforced gate below is static and credential-free, and the store
diff is an operator command rather than a job.

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
> Adding a line to a preflight's `missing` array is a two-store act.
>
> **And every secret a pull-request-reachable job consumes is asserted by a preflight** — so that a
> store gap is reported by NAME, in the first cheap job, instead of surfacing twenty minutes later
> as a script complaining about an argument. This half is enforced (below); the first half cannot
> be, because no CI credential can see the second store.

That invisibility is why this recurred. #2249 (2026-08) was the same defect, closed after
provisioning four names into two satellites. Since then Reinsurance added `MW_REGISTRY_KEY` and the
`AZURE_*` triple to its preflight, SocialMedia added `REGISTRY_PUBLISH_TOKEN`, and Manufacturing was
created — each into Actions only, each re-opening the hole, none visible to any review or gate.
#3399 is the second occurrence.

One smaller thing the audit surfaced, worth converging separately: the same credential is called
`MW_REGISTRY_INSTANCE_KEY` in Manufacturing and `MW_REGISTRY_KEY` everywhere else.

## The gate: every PR-reachable secret is asserted by a preflight

`.github/scripts/check-pr-secret-preflight.py` is the enforced half of the rule. It is **static** —
it reads workflow files, never a store — so it needs no credential and runs on every pull request in
every repository.

For each job that can run on a Dependabot `pull_request`, it collects every `secrets.NAME` the job
references (including names the caller hands to a shared `workflow_call` lane) and requires that
some pull-request-reachable job in the same repository binds that name to an env var and tests it
with `[ -n "${NAME:-}" ]`. Deciding *"can this job run on a Dependabot PR"* is a three-valued
evaluation of the job's `if:` against the facts of such a run: a condition that is provably false —
`github.event_name == 'push'`, the `publish-bake` shape, `github.actor != 'dependabot[bot]'` — takes
the job out; anything depending on run-time state (`needs.*`, `inputs.*`) is UNKNOWN and the job
stays IN. Unknown means reachable, because a missed job is a missed gate.

Where it runs:

- **core** — two steps in `dotnet-test.yml`, beside `check-workflow-timeouts.py`: the self-test
  first, then the tree.
- **every satellite** — through `node-repo-validate.yml`, which fetches this script from the
  platform at the caller's pinned `platform-ref` and runs it against the caller's tree. A satellite
  adopts the gate when it next bumps that pin, and the message names exactly what to add.

An exemption is a line in the caller's `.github/pr-secret-preflight-allow.txt`:

```
REGISTRY_PUBLISH_TOKEN  # passed to modules-*, whose publish step is `if: inputs.publish` — false on a PR
```

A reason is mandatory (at least 12 characters), and an entry whose name is no longer referenced by
any pull-request job **fails the gate**: a stale allow entry is how an exemption outlives the thing
it exempted. The other case the allow file covers is a name asserted inside a shared lane the repo
only *calls* — the guard cannot follow a cross-repo `uses:`, so it says so rather than assuming.

`secrets: inherit` into a reusable lane is refused outright: the guard cannot see which names the
callee consumes, so completeness cannot be proven — and inherit hands the callee every secret the
repo owns.

The self-test (`--self-test`, 19 cases) proves each check fires on its defect and stays silent on
its fix, and it runs *before* the real tree — an unproven gate is no gate. It was falsified both
ways on real trees when it was written: breaking core's two `MESHWEAVER_APP_ID` assertions took core
from 0 violations to 1 and exit 0 to exit 1, and restoring them returned it to 0; SocialMedia's real
tree reported 4 violations (5 of 9 required names asserted) and its real preflight completion took
it to 0 (9 of 9).

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

The whole audit in one command, from a checkout of the repo, with a credential that is admin on it:

```bash
python3 .github/scripts/check-pr-secret-preflight.py --root . \
        --check-stores --repo Systemorph/MeshWeaver.SocialMedia
```

`--check-stores` diffs the *consumed* set against the Dependabot store **by name** and prints the
exact `gh secret set` line per gap. It never reads a value, and it fails loudly rather than skipping
when a store cannot be listed or the listing is truncated — a store that could not be read is a
FAILED audit, not a clean one. Remember what it cannot see: a name present with an empty value.

## Remediating it

One command per missing name, same value as the Actions secret. `gh secret set` reads the value
from **stdin** when `--body` is omitted — there is no `--body-file` flag:

```bash
gh secret set <NAME> --app dependabot --repo Systemorph/MeshWeaver.<Repo>
```

Then re-run the Dependabot PR's workflow. The positive signal is the preflight's own success line,
not merely a green wall.

### Not everything on the list is a credential

Some of these names are **identifiers**, recoverable from a source an operator is already entitled
to read, and mirroring them needs nobody who holds a secret. Fourteen were mirrored this way on
2026-09-06:

| name | value established from | mirrored into |
|---|---|---|
| `AZURE_CLIENT_ID` | the `github-actions-bake` user-assigned identity — the only principal in the tenant with a federated credential for these repos (`repo:Systemorph/<repo>:ref:refs/heads/main`, in both the classic and the immutable subject format), holding exactly *Storage File Data Privileged Contributor* on the bake storage account | Reinsurance, SocialMedia, Crm, Education |
| `AZURE_TENANT_ID` | that identity's tenant (`az account show`) | the same four |
| `AZURE_SUBSCRIPTION_ID` | the subscription of the storage account each repo's own `vars.BAKE_PUBLISH_TARGETS` names | the same four |
| `MESHWEAVER_APP_ID` | `gh api /apps/meshweaver-cloud` — a public App id, and the App `auto-arm.yml` names by slug | MeshWeaver, Memex |

**Giving a Dependabot run the OIDC client id grants it nothing.** Federation is keyed on the token's
*subject*, and a Dependabot pull-request run's subject matches no federated credential — so
`azure/login` still refuses. The identifier is what lets the preflight get past *"this is not
provisioned"* to the honest answer.

What genuinely cannot be recovered is anything whose value is a secret: `*_PRIVATE_KEY`, `*_TOKEN`,
`*_PASSWORD`, `MW_REGISTRY_KEY` / `MW_REGISTRY_INSTANCE_KEY`, `ACR_USERNAME` / `ACR_PASSWORD`.
Do not guess one and do not copy one from a similarly-named secret in another repo — a wrong
credential fails as an authentication error deep in a lane, which is strictly worse than the missing
one it replaced.

## Related

- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — why an absent required context counts
  as satisfied, which is what makes an ill-considered exemption dangerous rather than merely lax.
- [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — the other core gate carrying the
  dependabot actor exemption, for the same credential and under the same merge-queue precondition.
- [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) — the shared
  `workflow_call` lanes every satellite's `preflight` feeds.
