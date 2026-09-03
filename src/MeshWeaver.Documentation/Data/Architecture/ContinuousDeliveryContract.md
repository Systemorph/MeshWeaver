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

> **Scope.** This contract is about ONE repo's set never being half-published. Coordination BETWEEN
> repos — who released what, and when a dependent repo may start building — is the
> [release event bus](../ReleaseEventBus), which is deliberately a different mechanism: repos
> announce releases as persistent facts and QUERY each other's state, rather than compiling or
> ordering each other's pipelines.

## The set, and why "partial" is the failure that hurts

| Repository | Architectures | What consumes it |
|---|---|---|
| `memex-portal-ai` | linux/amd64 + arm64 | **the self-updater** — the only repository it lists |
| `memex-migration` | linux/amd64 + arm64 | rolled to the same version as the portal, applies schema |
| `mw-plugin-test` | linux/amd64 + arm64 | the plugins repo's CI (ACR **and** a GHCR mirror) |
| `memex-portal-next` | linux/amd64 (by design) | pinned explicitly by deployments |

`.github/scripts/check-image-set.sh` **is** the definition of that set. It is asked by two jobs —
`gate` on the reconcile path, `verify-images` after a publish — and lives in one file precisely so
those two can never disagree. Both now pass the same, once-resolved plugins sha (see
[The set is keyed on a PAIR of commits](#the-set-is-keyed-on-a-pair-of-commits-not-on-cores-alone)). It asserts the **architectures**, not just tag existence: an image
index that lost a leg still resolves for one architecture, and a swallowed cancellation in
`Microsoft.NET.Build.Containers` is exactly how a leg goes missing while its job reports success.

A partial set is worse than no set: the self-updater sees a new `memex-portal-ai` version and rolls
the portal onto it, while the migration image or the bake certification for that commit does not
exist. That is how a portal increment the bake gate never certified reached production.

### The set is keyed on a PAIR of commits, not on core's alone

🚨 **The portal HOSTS live in `MeshWeaver.Plugins`**, so core's HEAD does not, by itself, say what is
in a portal image. A merge in that repo which edits a file shipping *in* the image — an
`appsettings.json`, a `.csproj` — changes what the image ought to contain while core's sha stands
still. Keyed on core alone the reconciler asked "does HEAD have a complete set?", answered *yes*, and
did nothing; the fix had **no producer that would ever rebuild it**. Not hypothetical: Plugins#814
fixed fresh-install engine activation at 14:54Z and the newest image predated it (#2622).

So `promote` phase A stamps a **pair tag** alongside the sha tag:

```
memex-portal-ai:<core-short>-p<plugins-short>
```

and `check-image-set.sh` takes the plugins sha as an optional second argument. Given one, the pair
tag is part of "the set" — so a plugins-only host merge makes the set *incomplete* on core's next
reconcile tick and the reconciler heals it **on its own**, instead of waiting for an unrelated core
merge or for a human to remember `rebuild`. Given no second argument it behaves exactly as before,
so every other caller is unaffected.

🚨 **The plugins sha is resolved ONCE, by `gate`, and threaded** to `promote` and `verify-images` as
a job output. This is a correctness condition, not tidiness. A run takes ~20 minutes; if
`verify-images` re-resolved, a plugins merge landing inside that window would make it look for
`…-pB` on an image the run correctly built from `A`, and a **successful publish would go red**. Cry
wolf, and the ledger becomes noise. Resolving once means the identity is minted by the run that owns
it — the same discipline as the staging tag — and the next reconcile tick re-resolves and correctly
finds the set stale, which is the desired behaviour at the right moment.

The `rebuild` input (#2825) remains as the operator escape hatch; it is no longer the only way this
case is noticed.

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

### 🚨 The third state the reconciler cannot see — a SUPPRESSED trigger

The paragraph above distinguishes two readings of an absent conclusion: *running* and *never ran*.
There is a third, and it is indistinguishable from the second by any amount of polling —
**the push that would have started CI was suppressed, so the check will never exist.**

GitHub performs an auto-merge as the identity that ARMED it, and **a push created with
`GITHUB_TOKEN` deliberately does not trigger workflow runs** — the recursion guard that stops
automation from re-triggering itself. So a lane that arms auto-merge with `secrets.GITHUB_TOKEN`
produces merges that land normally and start *nothing*: no `MeshWeaver Build and Test`, no
`Chart Gate`, no `Hosting Operator`. Measured on 2026-09-01 (#2916):

| main commit | merged by | `push`-event runs |
|---|---|---|
| `8cc1cc6e` | rbuergi (human) | Build and Test, Chart Gate, Hosting Operator |
| `19628536` · `b3e6ae65` · `04d25efa` · `dc6c5a45` | `github-actions[bot]` | **none** |

`gh api repos/Systemorph/MeshWeaver/commits/dc6c5a45d/check-runs` answered `0`. The reconciler read
`absent/none`, correctly declined to publish an untested tree, and waited — for six hours, across
four commits, while every install stayed on the previous image and every dashboard stayed green.

**This is the skip-trapdoor rule one level down.** `AGENTS.md` warns that GitHub paints a skipped
job the same colour as a passed one; here a *suppressed trigger* renders exactly like a slow one.
The absence is the symptom, and absence is the one thing polling cannot age out of, because
"not yet" and "never" produce byte-identical readings.

**The cure is at the credential, not at the gate.** Do not teach the reconciler to accept weaker
evidence — the green-tree marker (`refs/ci-green/<tree>/<epoch>`) would let CD publish, but
`Chart Gate` and `Hosting Operator` would stay silently dead, so that trades one invisible hole for
two. The merge must simply be performed by an identity whose pushes trigger workflows: a minted
GitHub App installation token (`actions/create-github-app-token`, requesting
`permission-contents: write` **and** `permission-pull-requests: write` — arming is the
`enablePullRequestAutoMerge` mutation, the merge itself writes to the branch). `auto-arm.yml` does
this, fails RED naming the grant when the App lacks it, and never falls back;
`ArmedMergeMustTriggerMainsPushLanesGuard` fails the build if anyone reintroduces `GITHUB_TOKEN`
there.

That guard reads configuration, which is normally the weak shape — deliberately, and the file says
so. The outcome ("did the merge start main's push lanes?") is unobservable from a pull-request run
by construction: it can only be seen on main, after the merge, and what you would inspect is
precisely what is missing. The credential is the only place a pre-merge check can stand, and it is
causal rather than correlated — with `GITHUB_TOKEN` the trigger is suppressed 100% of the time.

## The standing trap — verify the IMAGE, never the tick

CD's `workflow_run` trigger reacts to a **real push**, and — more exactly — to one that
**completes**. The consequences below all trip people up, and all are silent:

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

4. 🚨 **A merge burst cancels its own runs, and publishes nothing.** `dotnet-test.yml` groups by
   `build-test-${{ … || github.ref }}`, so every push to main shares the group `refs/heads/main` and
   each merge **cancels the in-flight run of the one before it** (#2316 — worth ~28% of runner
   demand, keep it). CD still **fires** on those runs — this workflow subscribes with
   `types: [completed]` and a cancelled run counts as completed — but the delivery gate keys on the
   required check **`Consolidate test results` reaching `success` for that SHA**, which a cancelled
   run never produces (and gating on the required check rather than the run's umbrella conclusion is
   deliberate — see the DELIVERY GATE comment). So CD wakes, decides "nothing will be built", and
   through a burst nothing publishes. Observed 2026-08-26: #2316 merged 12:55:25Z, the next
   **five** main runs cancelled back-to-back, 45 minutes with no completed run.

   Publishing only the burst's **tip** is the right outcome — intermediate commits never needed
   their own image set, and it is the same batching `CD_BATCH_WINDOW_MINUTES` wants. The hazard is
   that the burst must **end**: while merges keep arriving the tip run is cancelled every time, the
   push path stops publishing altogether, and the hourly reconciler quietly becomes the only
   publisher — which reads exactly like "CD is frozen".

   **Ordinary merges should still be batched** — the tip's image contains them all. The wait is owed
   only to a merge that must ship **on its own** (a CD fix, a hotfix under verification): merge it,
   wait for **that merge commit's** Build-and-Test to complete, and confirm the completed run's
   **head SHA is your merge commit** — "a run completed" and "the run for my commit completed"
   diverge during precisely the burst this is about.

   🚨 **And a merge cancels whatever is in flight, including a run another session is waiting on.**
   With several sessions merging into one repo, "wait for the run" only works if everyone waits —
   two sessions each merging politely still cancel each other. Check for an in-flight run someone is
   gating a deploy on before you merge, and hold if there is one.

   🚨 **A hold does not propagate to subagents.** An agent told to "root-cause and open a PR" will
   merge on green, because that is this repo's documented default. On 2026-08-26 one hold was broken
   three times — once by the operator and twice by subagents — each time by a correct fix landing at
   the wrong moment. Push the hold to every running agent explicitly and disarm any armed
   auto-merge: **a constraint is only as complete as the set of hands it reaches.** 🚨 Do not diagnose this from the tag alone: "no new tag" here has at
   least three causes — the batch window, a repoint/version-step failure on a run that *did*
   complete, and this. They stack, and naming only one leaves the other live.

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
.github/scripts/check-image-set.sh <short-sha> <plugins-short>   # ...including the pair tag (#2622)

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

## Property 1a — how the set already spans repos, and what the GUI move actually breaks

🚨 **Correction, 2026-08-26.** An earlier draft of this section claimed the shipped artefacts have
"two publishers on two independent triggers" and proposed unifying them. **That was wrong**, and the
evidence is in this pipeline's own comments — read them before proposing anything here.

**What is actually true:**

- **There is no node-package feed, and no lane that could publish to one.** Until 2026-08-30
  `MeshWeaver.Plugins/publish-packages.yml` packed every node package against a NuGet *floor* (dry
  run, publishing nothing); the maintainer retired it — in-mesh source runs inside the portal
  image, so its reference set is the image plus the modules the publication was sealed against
  (`node-repo-compile-check.yml`), and nothing a node repo builds reaches a package feed. So there
  is no second publisher, and **no package-release event to hang a webhook on**.
- **Consumers fetch BUNDLES from the registry portal** (`/api/plugins/bundles`) — *"assembled from
  the very bytes that portal runs"*. Distribution is bundles, not a feed.
- **Coordination already exists, and it is not a shared pipeline.** Each node repo's
  `node-repo-publish-bake` lane bakes and publishes its own content **under the SAME framework
  identity, from the SAME image**. That identity is what makes the sets composable.
- **`main-cd.yml` checks out no other repository — deliberately.** Its own words: *"there is not one
  `repository:` input in this file, so it could not compile them even by accident — and must never be
  given the chance."* Content a deployment receives ALREADY BUILT must be **adopted, not rebuilt**;
  recompiling it is the expensive half of a bake and re-does work another lane already did.

So "build everything, publish everything from one pipeline" is not a gap to close — it is a reversal
of a constraint this file states and defends. That may still be the right call, but it is a
**maintainer decision to change a deliberate design**, not a repair, and it should be argued against
the reason above rather than around it.

### What the GUI move DOES break

The real exposure is narrower and is not about publication at all:

1. **Nothing compiles the moved portal hosts.** `MeshWeaver.Plugins/ci.yml` builds named
   `MeshWeaver.*` module rows; `Memex.Portal.Gui` / `Monolith` / `Distributed` are not rows. Today a
   break is still caught by **core's image build** — which the deletion removes. Coverage reaches
   zero at that moment, silently, because nothing reports a check that is not there.
2. **The template needs a run with both trees — RESOLVED, and by moving the generator.** It COPIES
   six project directories and rewrites their `ProjectReference`s to `PackageReference`s. Three of
   the six leave with the GUI and three stay, so neither repo has all six. This paragraph used to
   conclude that moving the generator "does not help"; that was wrong, and the reason is a detail
   that did not exist when it was written: **MeshWeaver.Plugins' CI already clones the platform**
   (for `check-surface-manifest.py --core`), so it is the one place both trees are present. The
   generator now lives there and takes `--core <MeshWeaver checkout>`, with each of the six projects
   tagged by the root it resolves against.

   The trap that made this expensive to see: the generator reads **core's** tree, so its failures do
   NOT clear when files land in plugins — the dependency runs the opposite way from how it looks.

**The maintainer's ordering for the template** — `build all−template → package all−template →
build+test template → pack template` — follows from the rewrite: the generated solution resolves
against PACKAGES, so it cannot be built before they exist. The template is **downstream of
publication**, not another leg.

### If the pipelines are ever unified, the mechanism already exists

Every image leg pushes only a non-selectable `staging-<sha>-<run_id>`; `promote` applies the real
tags after all legs succeed. Extending atomicity means adding legs to that same `needs:` — never a
second gate that has to be kept in step with the first.

And the event rule composes only one way: plugins CI must NOT subscribe to a platform-released event
(verified — zero `repository_dispatch` in its three workflows). Whoever publishes, emits, on an
OBSERVED publication — the same reason `delivery-verdict` must not pass on an empty verdict (#2311),
and `FrameworkReleaseBroadcaster` fires on a real release rather than from a CI step that runs
regardless.

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

### The release EVENT — the pipeline calls memex; memex registers and publishes

**The contract (maintainer, 2026-09-03: *"end of github pipeline must call memex, which must
register release and publish event"*) is three sentences:**

1. **Every publishing pipeline ENDS with one call to memex.** Core's CD, after the image set is
   promoted, POSTs the signed platform build (`event: platform-build`) into the control instance's
   `Hosting/PlatformBuilds` inbox (`notify-platform-update`). Every node repository's
   `node-repo-publish-bake.yml` run, after its bundles are sealed for an identity, POSTs the signed
   publication record (`event: bundle-publication` — source, identity, commit, tester + portal image)
   into the same inbox (`register-publication`, its last job). Nothing runs after that call, and no
   pipeline sends a `repository_dispatch` to another repository.
2. **memex REGISTERS the release** as a durable node — `Hosting/PlatformBuilds/<version>` for a
   platform build, `Hosting/Publications/<identity>/<source>` for a bundle publication — the source
   of truth for "what is published for which identity" (what the self-update availability check reads).
3. **memex PUBLISHES the event** from that registration: `FrameworkReleaseBroadcaster` sends
   `meshweaver-framework-released` (platform) or `meshweaver-upstream-published` (bundle publication,
   `client_payload.version` = the identity) to the subscribed repositories — the repositories the
   control instance's `Hosting/Deployment` records name as registry sources. The subscribers' CI
   receives it, resolves both images from the version, builds and publishes for that identity — and
   ends by calling memex (1).

```
 pipeline (core CD | a node repo's publish-bake)        memex (control instance)              subscriber CI
 ───────────────────────────────────────────────        ────────────────────────              ─────────────
 promote / seal ✅                                       WebhookInbox Hosting/PlatformBuilds
   └─ ONE signed POST ──(platform-build |──────────────▶│ verify HMAC
      bundle-publication)… and FINISH                    ├─ REGISTER  Hosting/PlatformBuilds/<version>
                                                         │            Hosting/Publications/<identity>/<source>
                                                         ├─ subscribers = Hosting/Deployment records'
                                                         │              pluginRepos[].isRegistrySource
                                                         └─ PUBLISH   repository_dispatch ─────────────▶ on: repository_dispatch:
                                                            meshweaver-framework-released |               types: [meshweaver-framework-released,
                                                            meshweaver-upstream-published                        meshweaver-upstream-published]
                                                                                                          → bake for the version → seal → POST memex
```

Where the pieces are: the POST steps in `main-cd.yml` and `node-repo-publish-bake.yml` (this repo);
the inbox watcher, registration and broadcast in the Hosting module's `PlatformBuildInboxWatcher`
(MeshWeaver.Plugins, `Hosting/Deployment/Source`); the broadcaster in `src/MeshWeaver.GitSync`.
`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository` refuses a dispatch SENDER in any workflow
under `.github/workflows` — there is no ledger — and
`UpstreamBuildGateGuard.TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody` pins the lane's call.

**In flight, in this order (the contract is complete only when all have landed):** MeshWeaver.Plugins#1241
wires the platform half (broadcast + system identity + subscribers from the records) and is
observed firing before core withdraws its dispatcher (MeshWeaver#3185, this change); a Plugins
follow-up makes the watcher REGISTER the nodes named in (2) and handle `event: bundle-publication`
(register + `meshweaver-upstream-published`, dependency-scoped through the registry's package
`requires` graph so a publication cannot wake its own upstream); each node repository passes
`webhook-url` / `webhook-secret` to the lane when it moves its pin (the lane is RED, naming them,
until it does — a sealed publication memex was not told about is silent drift). Once Plugins receives
the platform event and publishes its own bundles on it, core CD's `plugins-bake` job is a SECOND
producer of the same publication and is removed — a follow-up, not part of this change.

**Fallback.** Each repository's `schedule` poll (`Resolve the bake target` in its ci.yml) still reads the
registry and bakes for the identity it finds, so a lost dispatch costs one delayed wave.

🚨 **The history, because the second dispatcher was justified by it.** The memex hop was designed on
2026-08-23 and was silent until 2026-09-03 — not because a mesh hop is unreliable, but because
**the broadcaster had no caller**: `FrameworkReleaseBroadcaster.Broadcast` was registered in DI and
invoked by nothing in either repository (the in-mesh call site carried a comment explaining why a
since-retired NuGet-floor lane could not compile the reference). On top of that the inbox watcher ran
with no identity, so on the control instance every background delivery was refused
(`AccessContext must never be null … hub=Hosting/PlatformBuilds`, memex-cloud 2026-09-03 04:22–07:41Z)
and replayed at the next arm. Core meanwhile grew `notify-dependents` — twice (2026-08-22, deleted;
2026-08-29 → 2026-09-03, deleted) — as "a second, independent path to the same event". Two emitters
for one event is precisely the cross-repo coupling the rule forbids; both defects in the memex hop
were fixed in MeshWeaver.Plugins before the core dispatcher was withdrawn.

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
observed on the SATELLITES — a `repository_dispatch` run whose payload carries `source: memex`, plus
the pin-bump PR — and in the control instance's log: `[PlatformBuilds] release broadcast for
<version>: N subscriber(s) dispatched.` Never a green tick in CD.

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
