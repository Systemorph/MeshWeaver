---
Name: The Continuous Delivery Contract
Category: Architecture
Description: What main-cd.yml guarantees about a published image set — all-or-nothing publication via unselectable staging tags, a promote job whose ordering makes rollback unnecessary, and an hourly reconciler that heals main's HEAD. Plus the standing rule: verify the IMAGE, never the green tick.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4"/><path d="M12 11v10"/><path d="M8.5 15.5l3.5 1.6 3.5-1.6"/></svg>
---

A merge to `main` publishes **five** container images to `meshweaver.azurecr.io`, and every
self-updating install rolls itself forward by reading one of them. [Release &
Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) describes what that set is and how installs
consume it. This page is about the *guarantee* — what `main-cd.yml` promises about the set, and the
two properties that make a partial or missing release impossible rather than merely loud.

## The set, and why "partial" is the failure that hurts

| Repository | Architectures | What consumes it |
|---|---|---|
| `memex-portal-ai` | linux/amd64 + arm64 | **the self-updater** — the only repository it lists |
| `memex-migration` | linux/amd64 + arm64 | rolled to the same version as the portal, applies schema |
| `mw-plugin-test` | linux/amd64 + arm64 | the plugins repo's CI (ACR **and** a GHCR mirror) |
| `memex-portal-next` | linux/amd64 (by design) | pinned explicitly by deployments |

`.github/scripts/check-image-set.sh` **is** the definition of that set. It is asked by two jobs —
`gate` on the reconcile path, `verify-images` after a publish — and lives in one file precisely so
those two can never disagree. It asserts the **architectures**, not just tag existence: an image
index that lost a leg still resolves for one architecture, and a swallowed cancellation in
`Microsoft.NET.Build.Containers` is exactly how a leg goes missing while its job reports success.

A partial set is worse than no set: the self-updater sees a new `memex-portal-ai` version and rolls
the portal onto it, while the migration image or the bake certification for that commit does not
exist. That is how a portal increment the bake gate never certified reached production.

## Property 1 — all-or-nothing publication

Each of the five build legs pushes its layers under a per-run **staging tag** and nothing else:

```
staging-<short-sha>-<run_id>
```

The staging tag is **unselectable by construction, not by convention.**
`VersionSelect.PlatformVersionTag` is

```csharp
private static readonly Regex PlatformVersionTag =
    new(@"^\d+\.\d+\.\d+([-+].*)?$", RegexOptions.Compiled);
```

and `PickTarget` filters every candidate tag through it, so a `staging-…` tag can never be selected
by any self-updater on any install, whatever the update policy. That regex is not decorative — it
exists because an all-digit git-sha tag once parsed as version `6943991.0.0`, sorted above every real
`3.x` release, and froze every portal on `ci.122`.

Only after **all five legs succeed** does the `promote` job apply real tags, and promotion is a
manifest-only operation (`docker buildx imagetools create`) — the layers are already in the registry,
so it costs seconds.

**Why staging-then-promote rather than build-to-archive:** the bytes have to reach the registry for a
push to happen at all, so pushing them to a staging tag costs exactly what pushing them to the real
tag costs. The only thing worth withholding is the **tag**, because the tag is the entire consumer
contract.

### Rollback by ordering, not by compensation

Tagging five repositories is not one atomic act, and a compensating delete cannot work anyway — for a
moving pointer like `latest`, untagging *destroys* it rather than reverting it. So `promote` is
**ordered so that a mid-flight failure is unobservable**:

| Phase | What it writes | If it fails here |
|---|---|---|
| **A** | identity tags (`<version>`, `<sha>`) on every repo **except** `memex-portal-ai:<version>` | no consumer-visible release; the residue is inert tags nothing resolves |
| **B** | moving pointers — `main` everywhere, `latest` on `mw-plugin-test`, the GHCR mirror | same: still nothing selectable |
| **C** | **`memex-portal-ai:<version>`** — one manifest PUT, the last thing the pipeline does | a single PUT either happened or did not; there is no half-armed state |

Phase C is the **arming write** and nothing else is, because `SelfUpdateHostedService` lists tags for
`memex-portal-ai` **only**, picks the newest `^\d+\.\d+\.\d+` one, and patches the portal *and*
migration Deployments to it. Everything the roll will need — the matching
`memex-migration:<version>`, the bake image that certified these node types — is already tagged by
phase A. **Do not move that step, and do not add anything after it.**

One deliberate coupling: the GHCR mirror sits in **phase B**, so a GHCR outage *blocks* the release
rather than silently leaving the plugins repo's `latest` stale. `check-image-set.sh` only observes
ACR, so a GHCR miss placed after the arming write would never be reconciled and nothing would notice.

## Property 2 — self-healing, and what it heals *to*

A failed CD run used to be terminal. Nothing re-attempted it, so the commit simply never got an image
and the hole closed only incidentally, when some later PR merged — which publishes the *later*
commit, so the failed commit's set is never completed, only superseded. The state did not heal, it
was papered over.

An hourly `schedule` (plus `workflow_dispatch`) now enters the same job graph through `gate`. It is
a **reconciler, not a retry**: it asks *"does main's current HEAD have a complete image set in
ACR?"* — observed state, answered by the same script `verify-images` uses — and drives a publish only
when the answer is no. It wraps no failing operation and suppresses no failure signal.

Four properties worth knowing before you rely on it:

**It heals HEAD, never the failed commit — and that is soundness, not laziness.** The version tag
comes from the *building* run's number, so re-publishing older code would mint a **higher**
`-ci.<n>` for it. Every install would then roll "forward" onto older code, breaking the monotonic
build-number invariant `VersionSelect` depends on to mean "pick the newest". HEAD is also the
declared desired state, and reconcilers converge on desired state rather than on history.

**It does not cry wolf.** It asks the registry *first* — a complete set means nothing is wrong
whatever the check says — and when the set is incomplete it reads the required check's **status as
well as its conclusion**. Those are not interchangeable: `conclusion` is `null` both while
Build-and-Test is running and when it never ran at all, so a conclusion-only reading would post "main
cannot be published" for a perfectly healthy commit on any tick landing shortly after a merge.
Running, or merged minutes ago with no check yet, means **wait silently**.

**It terminates.** Each tick is one attempt and does not re-trigger itself. Persistent failure is
bounded at **3 attempts per commit**, with the `ci-failure` issue as the ledger — no new state store.
The slot is consumed when an attempt *starts*, so a run that dies without reporting cannot buy
infinite retries; a new HEAD resets the budget naturally (the marker carries the SHA); on exhaustion
it stops, says so **once** (`🛑 Automatic healing STOPPED …`) and labels the issue `cd-unhealed`. A
successful heal comments `✅ Healed <sha>` on the same issue, so the ledger cannot only grow.

**It cannot publish an untested tree.** The `workflow_run` path still requires
`event == 'push' && head_branch == 'main'` — that gate is what stops a **fork's** `pull_request` run
(whose `head_branch` can also be `"main"`) from publishing untrusted code with this repo's secrets,
so it must never be relaxed. The reconcile paths do not read the event payload at all: they resolve
the target as the tip of *this* repo's `main` through the API and re-check `Consolidate test results`
on it. A fork's code can never be the tip of `Systemorph/MeshWeaver`'s `main`, so the safety property
is preserved by a stronger check rather than by a proxy.

## The standing trap — verify the IMAGE, never the tick

CD's `workflow_run` trigger reacts to a **real push**. Two consequences trip people up, and both are
silent:

1. **No Build-and-Test run on the merge commit at all** (a CI incident, a stalled queue). CD reacts
   to that workflow completing; with nothing to react to it sits `SKIPPED`.
2. **`workflow_dispatch` of Build-and-Test can never ship on its own.** `gh workflow run "MeshWeaver
   Build and Test" --ref main` runs, and genuinely tests the merge commit — so main shows a **green
   Build-and-Test**. But its `event` is `workflow_dispatch`, not `push`, so the `workflow_run` gate
   still skips. It is the most convincing possible "it shipped" signal, with no image behind it.

3. **Build-and-Test settled RED on main.** CD's push path then decides "⛔ nothing will be built" —
   and until 2026-08-22 that run ended **SUCCESS**, with the refusal visible only in its step
   summary. One flaky shard (`ProbeHubCostTest`, a probe hub racing its own dispose — fixed the
   same day) kept CD reporting green no-ops for hours while both production instances waited on
   the release it was not building. Since then `delivery-verdict` FAILS on that state and
   `alert-on-failure` pages it: **a red main is an incident for a continuously-delivered product,
   never a quiet skip.** The operator move is unchanged by the alarm: read the failing test; a
   flake → `gh run rerun <id> --failed` (a green rerun fires a fresh `workflow_run`, which
   publishes by itself); a real failure → fixing main IS the release path.

Two diagnosis cheats, both learned the hard way on 2026-08-22:

- **A CD run with ZERO jobs** ("This run likely failed because of a workflow file issue") means the
  workflow FILE is invalid — every run since the breaking merge produced nothing. The classic cause
  is a `needs:` naming a deleted job. Parse before pushing:
  `python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/main-cd.yml')); jobs=set(d['jobs']); print([(n,dep) for n,j in d['jobs'].items() for dep in ([j.get('needs')] if isinstance(j.get('needs'),str) else j.get('needs') or []) if dep not in jobs])"`
- **A green CD run whose jobs are all `skipped`** shipped nothing by decision — read the `Decide`
  step's last line for which branch fired, and believe that line over the tick.

**Neither of the first two is terminal now**, because the reconciler reads the *check on the
commit* rather than the event that produced it — so a dispatched Build-and-Test does eventually
lead to an image, within one reconcile tick. To kick CD by hand, use its own door:

```bash
gh workflow run main-cd.yml --ref main     # heals HEAD; cannot publish an untested tree
```

But the rule underneath is unchanged and applies to every "did it deploy?" question:

```bash
# 1. Does the image exist, for the commit you care about?
.github/scripts/check-image-set.sh <short-sha>      # the exact assertion CD itself makes

# 2. What is actually in the registry, newest first?
az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5 -o tsv

# 3. What is the cluster actually running?
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl get deploy -A -o custom-columns=NS:.metadata.namespace,IMAGE:.spec.template.spec.containers[0].image --no-headers | grep memex-portal-ai"
```

Installs then pick the image up **on the publication event, not on a timer** (#1773): each install
checks once at startup — catching anything published while it was down — and then once per build
completion it observes, across the whole `Admin/_Build` collection rather than one configured repo.
`SelfUpdateOptions.RetryInterval` (6 h) is only the backstop after a failed check, never the normal
path, and a `kubectl rollout restart` still forces an immediate check via the startup pass.

Together with the reconciler's hourly cadence, that bounds merge → running image at roughly
1 h of reconcile wait plus the ~20 min build — the reconcile tick, **not** the batch window, is
what sets that tail.

🚨 The same change makes **publication frequency equal to portal-restart frequency**: nothing paces
the rolls, so each published set restarts every install that selects it. That is the reason the tick
is hourly and not faster, and it is tracked as #1778 rather than papered over with a slower CD.

## Property 1a — the set spans BOTH repos (planned)

The set today is core's four images. The GUI moving to MeshWeaver.Plugins splits the shipped
artefacts across two repos with **two publishers on two independent triggers**:

| pipeline | trigger | publishes |
|---|---|---|
| core `main-cd.yml` | `workflow_run` on core main | the four core images |
| plugins `publish-packages.yml` | `push` to plugins main | plugin packages / bundles |

Two publishers cannot give the property above. All-or-nothing is what makes a partial publication
impossible, and it is enforced by ONE promote step observing every leg — a second pipeline with its
own trigger is outside that observation by construction. The image and the landed modules must move
together (a bidirectional binary break is what happens when they do not), so the direction is one
pipeline that builds and publishes both trees.

**The order, and why the template is last** (maintainer, 2026-08-26):

1. **Build all** − template
2. **Package all** − template
3. **Build template and test**
4. **Pack template**

`tools/generate-memex-template.cs` COPIES six project directories and rewrites their
`ProjectReference`s to `PackageReference`s. So the generated solution resolves against PACKAGES, and
it cannot be built before the packages it references exist — step 3 has nothing to resolve against
until step 2 has run. The template is therefore not "another leg": it is downstream of publication.

That also repairs what the split breaks. Three of its six inputs (`Memex.Portal.Monolith`,
`aspire/Memex.AppHost`, `aspire/Memex.Portal.Distributed`) leave with the GUI while the other three
stay. **Neither repo has all six**, so moving the generator does not help — it needs a run with both
trees present, which the unified pipeline gives it.

### The mechanism already exists — extend it, do not invent one

Every image leg pushes only a non-selectable `staging-<sha>-<run_id>` tag; `promote` applies the real
tags after all legs succeed. Extending the property means adding the plugins legs to that same
`needs:` — the atomicity is then the SAME gate, not a second one that must be kept in step with it.

### The release event: whoever publishes, emits

Plugins CI/CD must NOT subscribe to a platform-released event (verified 2026-08-26: zero
`repository_dispatch` in `ci.yml`, `publish-packages.yml`, `portal-next-image.yml` — the property to
preserve). That settles where the "plugins released" event comes from, and it is not a preference:

> Under a unified pipeline **core does the publishing**. For the plugins repo to emit
> "plugins released" it must LEARN that core published — and the only way it learns is by
> subscribing to core, which is the thing forbidden above. So the emitter is the publisher.

This matches the existing grain: the broadcast is emitted on an OBSERVED publication
(`FrameworkReleaseBroadcaster`), never by a CI step that runs regardless — the same reason
`delivery-verdict` must not pass on an empty verdict.

### What this costs, stated plainly

- **A red in either tree blocks both publishes.** That is what atomicity MEANS, and it is also a
  real cost: a plugins flake becomes a core release blocker.
- **Plugins-only changes need a way in.** `repository_dispatch` from plugins INTO core's CD — the
  opposite direction from the subscription forbidden above, so it does not reintroduce the coupling.
- **The reconciler's completeness check must learn the wider set.** It asks whether a commit has the
  complete four-image set; against a wider set it would otherwise declare a half-published commit
  healthy — the exact failure this property exists to prevent.

## Changing the pipeline

Adding a sixth image touches **three** places, and missing any one of them recreates the exact hole
this contract closes:

1. its own build job, pushing **only** the staging tag;
2. the `promote` job — identity tags in phase A, pointers in phase B (never after phase C);
3. `check-image-set.sh` — otherwise nothing ever asserts it shipped.

One known, deliberate wart: `memex-portal-next` hand-writes `3.0.0-ci.<n>` while every .NET leg
computes `3.0.0-rc1.ci.<n>`, so its version tag has never matched its siblings'. Nothing selects it
(the self-updater reads `memex-portal-ai` only; deployments pin portal-next explicitly), and changing
a published tag shape would break whatever is pinned today. It is documented at the line that
produces it rather than silently "fixed". This is also why `check-image-set.sh` identifies the set by
**short SHA** and not by version tag: the SHA is the one identity all four images share.

## After the promote — the bake publication and the dependent-repo dispatch

Two post-promote legs ride every armed release (#1660 WS3):

**`publish-bake`** runs the content the image itself embeds (the `Doc` tree — and **only** that:
node-repo content, Store packages and the samples trees arrive already compiled or are gate-only,
see [CI Content Bake](/Doc/Architecture/CiContentBake)) **inside the `mw-plugin-test` image this run
just built and promoted**, in two steps (#1763): `mw-plugin-test compile … --output /bake` — the
BAKE, a build step with no mesh in it — and then `mw-plugin-test … --seed /bake` — the GATE, a mesh
that CONSUMES that bake, so what renders and runs its `Tests` areas is the assembly about to be
published. A red gate still fails the job. It then copies the bundles onto the portals' shared
storage,
laid out `prebuilt-bundles/<framework-identity>/<source>/<bundle>.zip`
(`.github/scripts/publish-bake-bundles.sh`). Baking in the image is not an implementation detail —
it is the whole correctness argument. The framework identity is derived from the **binaries a host
ships**, so two different compilations of one source resolve different identities; until #1725 this
job published a Build-and-Test *artifact* — a different compilation — under an identity **no pod
ever resolves**, and every pod re-compiled ~80 platform NodeTypes on every boot behind a green
tick. Producer and consumer are now the same binaries, so the compatibility question cannot be got
wrong. Architecture is part of the identity too (the amd64 and arm64 variants of one image resolve
differently), so the bake is pinned to `--platform linux/amd64`, the architecture every AKS node
runs.

The same job also records **`prebuilt-bundles/_releases/<version>`**, a one-line marker holding the
framework identity this release resolved. That marker is the only way anything outside the image can
answer "which identity is release X?" — the identity is a property of the binaries, so it cannot be
computed from a tag or a commit — and it is what the [release availability
gates](../ReleaseGates) read before an environment is rolled or a dependent repo is built. It is
written on **every** run, deliberately outside the already-published skip below: the skip is the
common case for an unchanged surface, and a marker that rode along with the bundles would be absent
for every release after the first of a surface generation.

The identity stays equal across internal-only merges: when the identity's directory is already
**sealed** (the `_complete` sentinel the publisher writes strictly LAST, after every bundle), the
script skips with a notice instead of re-uploading ("rebuild only when we need to"). A publish that
died mid-way leaves no sentinel — the next run re-publishes wholesale, and the portal reader refuses
unsealed or torn directories, so a partial publication can neither freeze nor be seeded.
Each booting pod seeds its own identity's bundles (`PreWarm:PrebuiltBundleRoot` →
`ShippedPrebuiltBundles.SeedPublishedRoot`) before its NodeType sweep, and compiles only what CI
did not bake. **For satellite content this is measured, not aspirational**: on 2026-08-17 `memex`
(`3.0.0-rc4.ci.4049`, identity `s377941f549f721e01ac764e0fb8db84a`) adopted 68 prebuilt assemblies
from 31 sealed bundles in 18.9 s and compiled zero healthy types (`compiled=0`, `alreadyBaked=84`),
against 80 compiles / 64.8 s on the comparable boot before the satellite bakes existed.
🚨 **For the platform's OWN content it is still aspiration** — issue #1725: CI bakes it from the
Build-and-Test *build output*, while the pod resolves its identity inside the *shipped image*, so
the two identities differ and no pod can adopt the platform bake. The satellites are unaffected
because they bake INSIDE the shipped image, which is why only their publications match today. Its
configuration is **preflighted red, never skipped**: repo variable
`BAKE_PUBLISH_TARGETS` names the Azure Files targets (`<account>/<share>[/<base-path>]`,
whitespace-separated), and a missing value fails the job naming exactly that — a grey skip here
would silently restore the every-pod-rebakes-everything regression (#1347). There is no
"nothing to publish" branch any more: the bake happens in this job, so it either produces bundles
or fails red. Because it re-runs the content gate against the binaries that SHIP, a red here is a
genuine release defect — the images are already promoted, and nothing quietly ships less.

🚨 **The reconciler does not heal a failed bake publication.** `gate` asks the registry whether the
IMAGE set is complete, and by the time this job runs `promote` has already made it so — so a run
that publishes its images and then loses this job is not re-attempted, and the bundles land only on
the next successful CD publish (until then pods compile shipped content at boot, exactly as before
the lane existed). That asymmetry is why reaching the registry here is retried like the infra it is:
five attempts with backoff, then a loud error naming it a REGISTRY/INFRA failure. CD run
`32028644747` lost the job to a single `az acr login` → `[Errno 111] Connection refused` while four
sibling jobs in the same run reached the same registry fine. `alert-on-failure` still files the red
on the `ci-failure` issue, so a genuinely broken bake is never silent.

**There is no `notify-dependents`.** CD does not tell the node repos that a release happened, and
holds no credential that could. It was removed on 2026-08-22 after a fleet-stale incident, and the
reasoning is worth keeping because re-adding it will look like the obvious fix:

- It was **ADDRESSED notification where the design calls for a broadcast** — a publisher must not
  know its readers. `BAKE_SUBSCRIBER_REPOS` was a hand-maintained second copy of a graph memex
  already holds, and a missing entry failed **silently**.
- It needed a cross-repo **write** credential (`DEPENDENT_DISPATCH_TOKEN`, or a GitHub App with
  `contents: write` on every satellite) purely to say "something happened".
- It was **never provisioned**, so for its whole life it printed a not-configured notice and did
  nothing — while the fleet stayed a release behind with every check green. The satellite→satellite
  variant (`dependent-repos` / `meshweaver-upstream-published`) is gone for the same reasons.

**The wave is PULLED.** A release is a fact about the registry: memex publishes the released image,
and each node repo's **scheduled** run reads it and rebuilds for the identity it finds.

```yaml
on:
  repository_dispatch:
    types: [meshweaver-framework-released]   # hand-fire; carry client_payload.version
  schedule:
    - cron: "17,47 * * * *"                  # 🚨 the actual mechanism
```

> 🚨 **A node repo without the `schedule` trigger never bakes for a released identity.** It bakes
> its pin, on its own pushes, forever — and the only symptom is an instance HELD on bundles from
> that repo's source. Check the schedule first.

> 🚨 **A hand-fired dispatch must carry `client_payload.version`.** Some callers resolve the released
> image from it and fail hard without it:
> `gh api repos/Systemorph/<repo>/dispatches -f event_type=meshweaver-framework-released -f 'client_payload[version]=<released version>'`.
> Firing without a payload is how a "remedy" turns into a red bake that changes nothing.

### The release EVENT — pushed, but from memex, and now failable

The pull above is the guarantee. Since 2026-08-23 it is also **broadcast**, so the wave is prompt
instead of arriving up to a schedule interval late — and the broadcast is placed so that neither
objection to `notify-dependents` comes back:

```
CD (this repo)                       memex (the control instance)          the node repos
──────────────                       ────────────────────────────          ──────────────
promote ✅                            WebhookInbox: Hosting/PlatformBuilds
   │                                        │  (allowlist + size cap only)
   └─ notify-platform-update ──POST────────▶│
      HMAC-SHA256 over the RAW body         ├─ PlatformBuildInboxWatcher verifies the HMAC
      (secrets.PLATFORM_WEBHOOK_SECRET)     │     against Hosting:PlatformWebhookSecret
                                            ├─ PlatformPinUpdater → MW_IMAGE_DIGEST bump PRs
                                            └─ FrameworkReleaseBroadcaster ──dispatch──▶ ✅ rebake
                                                 (the GitHub App memex already holds for GitSync)
```

- **No credential in the release path.** The platform holds no write access to any satellite and no
  PAT; it signs one POST. The fan-out uses the App memex already has.
- **No list in this repo.** The subscriber set lives in memex's own Hosting fleet registry. The
  vestigial `BAKE_SUBSCRIBER_REPOS` repo variable was **deleted on 2026-08-25** — a leftover that
  looks like the live subscriber list is worse than none.

🚨 **The notify job is a GATE, not a reporter (#2235).** It was written reporter-class — "losing one
notification costs one delayed rebake wave" — with an input-shaped `if [ -z "$SECRET" ] … exit 0`
and a `::warning::` on every non-2xx. Result: **zero releases broadcast between 2026-08-22 and
2026-08-25, and a green tick on every promote.** Now `preflight` asserts `PLATFORM_WEBHOOK_URL` +
`PLATFORM_WEBHOOK_SECRET` RED naming what to provision, every non-2xx is `exit 1` with a message
naming which of the three causes it is, and both jobs are in `alert-on-failure`'s `needs` so the
failure is filed rather than merely rendered. It runs after `promote`, so failing it cannot
unpublish anything — the images ship and the installs still self-update; what changes is that
"nobody was told" stops looking like success. `PlatformReleaseNotifyGuard`
(`test/MeshWeaver.Documentation.Test`) pins all of it.

🚨 **A 2xx still does NOT prove the wave ran.** The inbox is deliberately dumb, so 2xx means
*stored*. The half CD cannot see is the shared HMAC: an unset or mismatched
`Hosting:PlatformWebhookSecret` on the control instance (Key Vault → `Hosting-PlatformWebhookSecret`;
see `deploy/aks/envs/example/secretproviderclass.yaml`) makes the watcher drop every delivery as
unverifiable, and the POST still answers 2xx. The close condition for the mechanism is therefore
observed on the SATELLITES — a `repository_dispatch` run plus the `MW_IMAGE_DIGEST` bump PR — never
a green tick in CD.

Provisioning state (2026-08-17): the satellites' **publish** credentials ARE provisioned. The Azure
managed identity `github-actions-bake` (in the cluster's resource group) holds *Storage File Data
Privileged Contributor* on the portals' storage account, carries **8 federated credentials — the
four satellite repos × two subject formats** (below) — and `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` /
`AZURE_SUBSCRIPTION_ID` are set on all four. A red publish-bake was designed debt until 2026-08-17
and is a real failure after it.

**The cascade is a DAG, not a fan-out.** A satellite that depends on another declares
`upstream-sources` and, if its upstream has not published for the target framework, exits without
building — and comes back on its own schedule. See [Release Availability Gates](../ReleaseGates).

### 🚨 Register BOTH OIDC subject formats — the classic one AND the immutable one

GitHub is rolling out **immutable** OIDC subjects that embed numeric ids
(`repo:Systemorph@77832550/<Repo>@<repoId>:ref:refs/heads/main`) alongside the classic name-based
form (`repo:Systemorph/<Repo>:ref:refs/heads/main`) — and **which one a repo presents varies per
repo inside the same org at the same moment.** Measured 2026-08-17: Education and SocialMedia
presented the immutable form while MeshWeaver.Plugins presented the classic one, so "tidying" all
four to immutable broke Plugins with

```
AADSTS700213: No matching federated identity record found for presented assertion subject '<…>'
```

**The durable arrangement is two federated credentials per repo — one classic, one immutable** —
each still precisely scoped to repo + branch (no wildcard subject, no org-wide credential). It
costs nothing, and it survives a repo flipping format under you:

```bash
REPO=MeshWeaver.Plugins           # the repo you are wiring
SHORT=plugins                     # its short name, used only in the credential's --name
REPO_ID=$(gh api repos/Systemorph/$REPO --jq .id)
ORG_ID=$(gh api orgs/Systemorph --jq .id)
ISSUER=https://token.actions.githubusercontent.com
# BOTH of the following, never just one:
az identity federated-credential create -g <aks-resource-group> --identity-name github-actions-bake \
  --name "gh-$SHORT-main-classic" --issuer "$ISSUER" \
  --subject "repo:Systemorph/$REPO:ref:refs/heads/main" --audience api://AzureADTokenExchange
az identity federated-credential create -g <aks-resource-group> --identity-name github-actions-bake \
  --name "gh-meshweaver-$SHORT-main" --issuer "$ISSUER" \
  --subject "repo:Systemorph@$ORG_ID/$REPO@$REPO_ID:ref:refs/heads/main" --audience api://AzureADTokenExchange

# What is registered today (expect 2 rows per repo, 8 in total):
az identity federated-credential list --identity-name github-actions-bake -g <aks-resource-group> \
  --query "[].{name:name,subject:subject}" -o tsv
```

When `AADSTS700213` appears, **copy the presented subject verbatim out of the error message and add
a credential for it, KEEPING the existing one.** Deleting the other format is what turns one repo's
green lane red.

## See also

- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the two channels, the update policy node, and how each install applies an update.
- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the version number comes from.
- [Deployment](/Doc/Architecture/Deployment) — the route router (AKS vs Container Apps).
- [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) — what the `mw-plugin-test` leg is for.
