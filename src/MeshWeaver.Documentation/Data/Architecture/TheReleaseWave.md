---
Name: The Release Wave — one emitter, and who resolves the digest
Category: Documentation
Description: How a publication becomes a repository_dispatch across the plugin fleet — the single emitter rule, the image/digest contract that puts resolution on the RECEIVER, and the transitional gap left by retiring the GitHub-to-GitHub job
---

*Historical framing, kept because it explains the wave's shape:* a module was built against a
platform **pin**, so when the platform (or an upstream catalog) published, every dependent repo had
to rebuild or its portals read `FrameworkDeclined` and adopted nothing. Neither half holds as of
2026-09-12/13: no repository carries a pin (each resolves the newest sealed set at run time — see
*After phase 1* below), and an installation's default `Modules:VersionStrictness = Family`
(`PrebuiltAdoptionPolicy`) adopts a bundle sealed for another identity of the same major line when
its floor is satisfied and its type links resolve — so a dependent that has not rebuilt keeps
serving the previous sealed publication, and "no bundle yet for this identity" is an ordinary,
bounded state rather than an adoption failure. What the wave still decides is *when* the next
publication for a new identity exists at all. The mechanics below are unchanged by either. The mechanism that wakes them is the **release wave**: one `repository_dispatch` per
subscribed repository.

> **Under the ladder** (policy `platform-backwards-compatibility`): a normal platform build needs no
> wave and no re-seal — publications are keyed on the compatibility key `c<major>e<epoch>`, shared by
> every build of one epoch, so a plugin publication keeps serving across platform rolls. A plugin
> seal for a new platform is a precondition of anything only behind a DECLARED break, and then the
> roll is held per plugin, by name — [Deploying Across Platform Versions](../DeployingAcrossPlatformVersions).

This page exists because the wave has been mis-diagnosed twice in one day, in opposite directions —
once by blaming a receiver's pin, once by blaming an emitter that did not exist. Both mistakes are
cheap to repeat, because **the evidence for "who sent this" is not in the repo that received it.**

## 🚨 The wave has exactly ONE emitter — and which one it is depends on a PIN

> Maintainer, 2026-09-03: *"None of the top-level repos should have any dependency to anyone else.
> It must be event based: (1) memex issues an event that something has a new version; (2) GitHub
> subscribes to this and triggers the build. Core publishes an event and finishes."*

That directive **retired** the GitHub→GitHub emitter — the `dispatch-dependents` job in this repo's
`node-repo-publish-bake.yml`. On `main` the input survives only as a `RETIRED 2026-09-03` stub that
is *ignored*, so a caller pinned to an older lane still validates while it moves its pin.

**And that is the trap.** A reusable workflow is consumed at a `@<sha>` pin, so retiring a job on
`main` retires it for *nobody* until each caller moves. A repo pinned before the retirement still
runs the old job, which still dispatches — a **zombie emitter**, live in a caller's `ci.yml` rather
than anywhere you would grep for it.

**So: never answer "where did this dispatch come from?" from `main`.** Read the emitting repo's
pin, then read `node-repo-publish-bake.yml` *at that pin*:

```bash
grep -n 'node-repo-publish-bake.yml@' <repo>/.github/workflows/ci.yml
git -C ~/code/MeshWeaver cat-file -p "<pin>:.github/workflows/node-repo-publish-bake.yml" \
  | grep -n 'dispatch-dependents:'
```

A corollary worth stating because it has already cost a wrong issue: **an in-mesh node is not the
emitter merely because no committed source explains the dispatch.** In-mesh `.cs` compiles at
runtime and is invisible to `dotnet build` and to a `.cs` grep, which makes it a tempting suspect.
Before accusing it, `get` the live node and diff it against `origin/main`. On 2026-09-03
`Hosting/Deployment/Source/PlatformBuildInboxWatcher` (v7) matched the repo exactly and contained
no wave emitter at all — the dispatches were the zombie job above.

## The image/digest contract — the RECEIVER resolves

The dispatch payload carries both forms, and they are not redundant:

| field | what it is |
|---|---|
| `image` | the full tester-image reference the bake used — **authoritative** |
| `platform_image` | the portal image this bake compiled against (MeshWeaver#3022) |
| `digest` | a **convenience**: the part after `@`, and **empty when `image` was a tag** |

```sh
# A receiver's own gate wants the bare digest (its image-digest input); the bake wants
# the full reference. Carry both — the digest is the part after '@' when the image was
# resolved by digest, empty when it was a tag (then the receiver resolves it itself).
digest=""; case "$IMAGE" in *@sha256:*) digest="${IMAGE#*@}" ;; esac
```

🚨 **An empty `digest` beside a present `image` is the contract WORKING, not a contract break.**
The parenthesis — *"then the receiver resolves it itself"* — is the receiver's obligation, and a
receiver that refuses instead throws away well-formed waves. MeshWeaver.Manufacturing did exactly
that for ten consecutive red `main` runs on 2026-09-03 before its preflight was taught to resolve
`client_payload.image` (Manufacturing#50).

**Resolving is not the same as falling back to the pin, and the difference is the whole point.**
The pin *caps* what the gates can see, so gating against it reports a green release-follow for a
framework nobody checked. Resolve the wave's own image and fail LOUD when you cannot:

```sh
digest=$(docker manifest inspect -v "$WAVE_IMAGE" \
  | jq -r 'if type == "array" then .[0] else . end | .Descriptor.digest // ""')
```

Not `imagetools inspect --format '{{.Manifest.Digest}}'` — that template reads a member an OCI
manifest does not carry, so it yields nothing and every run takes the fail-loud branch.

## The transitional gap (open as of 2026-09-03)

The directive's end state is: the lane ends with **one signed POST** of the publication record to
memex (`webhook-url` / `webhook-secret`), and **memex** emits `meshweaver-upstream-published`.

The receiving half exists — memex's generic webhook inbox, and
`Hosting/Deployment/Source/PlatformBuildInboxWatcher` draining it. The **emitting half does not**:
`ParseBuild` accepts only `event: "platform-build"` and logs *"ignoring non-build event"* for
anything else, then deletes it. `FrameworkReleaseBroadcaster.Broadcast` already takes the
`eventType` and `payload` arguments a `bundle-publication` branch would need; nothing calls them.

**Therefore the pins must move in this order**, and moving them early is worse than leaving them:

1. Write the `bundle-publication` branch (parse the record, broadcast
   `meshweaver-upstream-published` carrying `source`, `image`, `platform_image`, `digest`,
   `identity`, `version`, `sha`), with a test pinning the **payload**, not just the log line.
2. Provision `webhook-url` / `webhook-secret` on each publishing caller.
3. Then move each caller's pin past the retirement.

Move a pin before step 1 and the publication POSTs to an inbox that discards it: the zombie
emitter stops, nothing replaces it, and **the wave dies silently** — every dependent falls back to
its daily schedule poll, which is exactly the "absence of evidence read as evidence" failure the
whole lane is built to refuse.

## After phase 1 (2026-09-12) — measured 2026-09-13: the wave moved, it did not stop

The transitional gap above closed: the `bundle-publication` branch exists
(`PlatformBuildInboxWatcher.BroadcastPublication`, MeshWeaver.Plugins), every publishing caller
posts the record, and phase 1 of the fleet CI refactor (Plugins#1707/#1709, Reinsurance#198,
Crm#91, SocialMedia#178, Manufacturing#79; the process page is MeshWeaver.Plugins
`Hosting/BuildAndReleaseProcess`) put `meshweaver-framework-released` behind a switch that is off
by default and dropped it from the satellites' receivers. Its stated expectation was that the
whole `repository_dispatch` share of the fleet's bill (≈15,000 minutes a day) goes away.

**It did not — measured by run over the first 19 hours, 2026-09-12T12:00Z → 09-13T07:08Z:**

| repo | `upstream-published` runs | `framework-released` runs | core CD runs in the window |
|---|---|---|---|
| Crm | **23** | 0 | 41 |
| SocialMedia | **24** | 0 | 41 |
| Manufacturing | **23** | 0 | 41 |
| Education | 18 | **42** — Education#320 is unmerged; `ci.yml:28` still lists the type | 41 |
| Reinsurance | **0** | 0 | 41 |

The emitter is the one this page names: core CD's own `plugins-bake` calls the reusable lane with
`webhook-url`, so its `register-publication` posts a `plugins` `bundle-publication` **on every core
build** — a fresh framework identity each time, so `IsRepeat` never fires — and `DependentsOf(plugins)`
is every satellite that declares `plugins`. Twenty of twenty core register jobs succeeded in the
window, and each dependent's run started within seconds of one (core 06:22:40Z → Crm 06:22:41Z).
So the per-build wave now wears `meshweaver-upstream-published`; only its name changed. Reinsurance's
zero is the one anomaly — it declares `plugins crm` and lists the type, so its registration on the
control instance is the next thing to read. Education's 42 say the `framework-released` emitter is
also still live for any receiver that lists the type.

Whether one wake per core build is wanted is Roland's decision (the same identity churn is costed in
[Framework Identity Churn](../FrameworkIdentityChurn)); what this section settles is that the phase-1
expectation was a prediction, and the count replaced it.

### The pin this page opens with is gone

*"A module is built against a platform pin"* — no longer. Audited on every repository's `main` on
2026-09-13 (`.github/workflows/ci.yml`, uncommented lines): **no** `MW_PLATFORM_REF` /
`MW_PLATFORM_SET` / `MW_IMAGE_DIGEST` / `MW_PORTAL_IMAGE_DIGEST` literal in any of the six (the one
remaining reference is `FREEZE: ${{ vars.MW_PLATFORM_REF }}`, the incident-freeze repository
VARIABLE the resolver honours by design); every `uses: Systemorph/MeshWeaver/…` lane ref is `@main`
(0 of 43 pinned to a sha); `check-platform-pins.py` is invoked nowhere. Every repository resolves the
newest sealed set at run time with `resolve-platform.py` — and **carries its own copy of it**: seven
copies at six distinct sizes on 2026-09-13 (core 68,150 B; Crm 69,027; Reinsurance 69,035;
SocialMedia 69,035; Manufacturing 69,037; Plugins 60,365; Education 46,786). The reusable lanes fetch
**core's** copy at `scripts-ref` for their own resolution, so the copies decide only what a
satellite's *own* jobs resolve — which is exactly the file the same-drift argument on Plugins#1565
was about.

**What the six copies actually differ in — measured at the CODE level** (both files parsed,
docstrings dropped, comments gone by construction, ASTs unparsed and diffed), and whether the
difference changes an answer on the inputs that repository passes:

| copy | raw lines vs canonical | code lines | what differs | changes an answer? |
|---|---|---|---|---|
| Crm, Reinsurance, SocialMedia, Manufacturing, **Education** | 173 | **6** | three string literals: the `User-Agent` header value, one log line, one step-summary sentence | **no** — none is read by the resolution |
| Plugins (fork, 2026-09-12, #1694) | 1,031 | **520** | ADDS `ceiling_for`/`main_passed_ceiling`/`run_jobs_of` (a pull request resolves the newest sealed set that Plugins `main` has already PASSED on) and `PublicationSource`/`publication_source`/`ProvenanceUnavailable` (the chosen set's sha and release read from the platform-bake job's own final publication receipt); LACKS `platform_version` | **yes, by design** — on `pull_request` runs the ceiling can choose an older set than the canonical would |

🚨 **Education's row is the one that moved, and #4171's own body is now stale about it.** That body
recorded Education at 233 answer-changing code lines from a 2026-09-11 vintage; re-measured on
2026-09-13 against the same canonical it is **6**, the same three string literals as the other four.
It converged on its own. The reading to take from that is not "the table was wrong" but the standing
one: a drift figure is a MEASUREMENT with a date on it, and a satellite's copy moves between the
filing and the flip — so re-run `check-resolver-copy.py` before acting on any number written here.

So five copies are behaviourally the canonical and one is a fork with policy the canonical
did not have. **The guard** — `.github/scripts/check-resolver-copy.py`, run by `node-repo-validate`
on every satellite PR and push — compares the copy to the canonical fetched at the lane's
`scripts-ref` at the code level, prints the functions that differ, and is **advisory until
2026-09-15T00:00:00Z and red from then** (`RED_FROM` in the script): a canonical fetched at `main`
is live on merge for every caller, so the fleet sees the finding for a day before it can fail on
it. A repository with no copy passes — that is the end state. The freeze (`MW_PLATFORM_REF`) is
untouched: the guard compares files and never runs the resolver.

### Following your own `main`: the ceiling, now an option on the canonical

🚨 **A deliberate difference belongs in the canonical as an OPTION, never in a fork** — and the
guard is what makes that rule enforceable rather than advisory. Plugins' ceiling is therefore in
`.github/scripts/resolve-platform.py` itself, off unless asked for:

| flag | what it does |
|---|---|
| `--passed-on-main OWNER/REPO` | reads that repository's newest successful `ci.yml` push runs on `main`, takes the core-CD run number each one's `Platform for this run` annotation names, and caps the choice at the highest of them |
| `--passed-ceiling N` | the same cap, handed straight in — a re-resolving job passes the `ceiling` output its run's `platform-ref` job already established, so the main-run read is paid for once per run |

The rule it expresses: **a pull request resolves the newest sealed set that repository's own `main`
has already passed on.** When core seals a set that regresses the repository, `main` goes red on it
and every open pull request keeps building on the last set main passed — before it, one such set
reddened every open PR at once, four times in 24 h, 91 PR-hours exposed.

Three properties are load-bearing and each has a self-test case that fails without it
(`resolve-platform.py --self-test`, whose own summary line prints the suite's size — a total repeated
in prose has nothing keeping it true, and this one had gone stale):

- **Opt-in.** `choose(..., passed_ceiling=None)` — every caller that does not ask — takes exactly
  the path it took before. The self-test proves this as a PAIR on one fixture: the same runs and the
  same registry, one argument different, opposite answers.
- **A freeze overrides the ceiling, including an unreadable one.** `MW_PLATFORM_REF` is an
  instruction for an incident, and the likeliest moment to need it is precisely when `main` is red
  and has passed nothing recently — so a freeze skips the ceiling entirely rather than being checked
  against it.
- **"main has passed nothing" is a REFUSAL, not a fallback.** Falling back to the newest sealed set
  would put every pull request back on an unvouched set, which is the thing the rule exists to
  prevent. The refusal names what it read and points at `MW_PLATFORM_REF`.

A run held back by the ceiling says so: `lag` on the `Chosen`, an output row, a `Platform lag
(pull requests follow main)` notice and a summary row, each naming BOTH set ids — so "why did my
core fix not show up in my PR?" is answered from the run's own log.

🚨 **And it says the harder half too: that the run cannot prove anything about the set it passed
over.** Naming both ids says the lane is *behind*; it does not say what an author has to act on. When
`main` is behind **because the set moved**, the lane holds the pull request on the set from *before*
the break — so a pull request whose whole purpose is to fix that regression can go green having
exercised none of it, and a fix that names any symbol the newer set introduced cannot compile in the
lane at all. Measured on the pull request fixing one such outage: `CS0103` / `CS0117` / `CS1061`
against the older set, for the very API the change was about. The consequence is a constraint on the
*shape* of the fix imposed by the lane rather than by the problem — a set-move regression whose only
honest fix requires the new API has no green path — so the `lag` notice now states it and tells the
author to verify against the newer set outside the lane and say so in the pull request body.
`resolve-platform.py`'s self-test asserts the sentence (`CANNOT PROVE`) rather than only that both
ids appear; removing it fails exactly ONE case, which is the load-bearing fact.

**This deliberately makes the deadlock VISIBLE rather than escapable, and that is the whole design.**
The ceiling itself is untouched: a pull request that silently resolved a newer set than `main` has
passed would be testing against bytes `main` has never validated, which is the hole
[#1826](https://github.com/Systemorph/MeshWeaver/issues/1826) /
[#4265](https://github.com/Systemorph/MeshWeaver/issues/4265) record, and *refusing rather than
falling back* stays the correct default. Whether the lane should additionally offer an explicit,
per-pull-request **opt-in** to the newest sealed set when `main` is red on it is a separate and open
question — an opt-in is a skip-trapdoor wearing a justification, reached for under exactly the
pressure that makes people careless, and the fleet's own rule is that a gate never lets the caller
decide whether it applies. That decision is tracked on
[#4348](https://github.com/Systemorph/MeshWeaver/issues/4348) and is not taken here.

### Verifying where a set came FROM: `--verify-source`

The second thing Plugins' fork carried, and the second option on the canonical. By default the chosen
set's core commit is the publishing run's `head_sha` and its release is what `Directory.Build.props`
declares at that commit. **Neither is a statement the publication itself made**, and the publishing
lane reuses CONTENT-ADDRESSED builds from earlier runs — so the run that published a set is not
necessarily the run that BUILT its bytes, and `head_sha` can put a newer commit on older bytes. That
is [#4158](https://github.com/Systemorph/MeshWeaver/issues/4158)'s defect one level up.

`--verify-source` takes both from the platform bake's OWN final publication receipt — the
`bake published: source=meshweaver-content source-sha=… release=… arch=… identity=… bundles=N …`
line `publish-bake-bundles.sh` writes AFTER publication, convergence and the release-marker writes.
That line names the gate-selected source and release; workflow metadata does not.

- **A set that cannot attribute itself is PASSED OVER, never taken unattributed.** A missing,
  duplicated, malformed or inconsistent receipt raises `ProvenanceUnavailable`, the reason is
  recorded in `skipped`, and the resolution continues at an older VERIFIED set. Under a freeze the
  same condition is fatal — **for the run the freeze NAMES**; see below, this was #4242.

🚨 **"The freeze names this run" is a question, and `if freeze_kind:` is not it.** Every *"a freeze
is an instruction, not a preference"* escalation used to be spelled that way — correct only because
the two filters at the top of the scan had already narrowed it to one run. `--verify-source`
deliberately does NOT apply the head-sha filter (the set's real sha is the receipt's, unknown until
the jobs are read), so the scan reaches runs the freeze does not name, and the FIRST unsealed one
aborted the whole resolution with a sentence that was simply false:

```
--freeze 7ee11bc7… --verify-source
::error:: the freeze names main-cd #8531 (core e0e4aeff3), which is not a sealed set …
```

`7ee11bc7` is the head of main-cd **#8506**; #8531 was merely the newest run in the scan. Measured
against live core CD on 2026-09-13 — and it made `--verify-source` unusable during an incident
freeze, which is exactly when resolution has to keep working. The predicate is now
`freeze_names_this_run`, which NARROWS and never widens: a set freeze is already one run number and
a sha freeze without verification is already one head sha, so both answer exactly as before; the
only case that changes is a sha freeze WITH verification, where a run that cannot produce a receipt
carries no evidence that it is the frozen one and the scan continues.

🚨 **An ABSENT receipt is not a DISAGREEMENT, and reporting one as the other cost an investigation.**
`publication_source` used to answer *"successful platform bakes disagree on source/release (found 0
distinct receipts)"* for a run that has **no successful platform bake at all** — an absence in the
vocabulary of a disagreement. `choose` never asks about such a run (it skips an unsealed one first),
but anything probing runs directly does, and on 2026-09-13 that sentence was read off eleven
ordinary non-publishing `main-cd` runs and reported as a fleet-wide bake defect. Re-measured the
same day over **main-cd 8505–8531**: **20 of 27 runs are UNSEALED** (they never reach the receipt
read), **all 7 sealed runs are attributable, and NONE disagrees.** The two states are now two
sentences, and the disagreement one names the receipts it found.

One of those seven is worth reading, because it is the feature working rather than failing: **8514's
head is `7be4af59` and its receipt names `371f289b`** — which is 8513's head. The bake reused the
content-addressed build from the previous commit, so the receipt is right and the run head would
have been wrong. That is precisely what `--verify-source` exists to see.
- **A freeze BY SHA is matched against the receipt's sha**, which is the point: the run's head sha is
  a different value and would match nothing.
- **The log read is the only text this script ever fetches, it is bounded** (`MAX_LOG_BYTES`; over the
  cap is a refusal, never a truncated parse that could match the wrong receipt) **and the token is
  sent UNREDIRECTED** — a job-logs path answers a 302 to signed storage, and urllib would otherwise
  forward this token to a host that is not GitHub and does not need it.

🚨 **It grants nothing to anyone who does not ask, and that is executable rather than asserted.** The
self-test's default case is run against a fetch that RAISES on any path ending `/logs`:

> `DEFAULT (no --verify-source): head sha, and NO job log is fetched at all`

Ungating the read reds that case by name (`expected a choice, got RED: a caller that did not pass
--verify-source read a job LOG`) — along with 41 others, which is the same statement from the other
side: the read is not a neutral addition to the default path.

So the canonical needs no credential a caller does not already hold: the logs of the run it reads are
fetched with the CALLER's own token, only when the caller passes the flag, exactly as the fork did.

## Reading a wave, in order

1. **Who emitted it?** The receiver's run says `repository_dispatch` and an actor; it does not say
   which repo. Find the caller whose `bake-source` matches `client_payload.source`, then read its
   pin (above) — not core `main`.
2. **Who subscribes?** The old job filtered on the dependent's own `ci.yml` declaring the source
   under `upstream-sources:` / `upstream-seed:` — a line-anchored match, so a mention in a comment
   does not subscribe a repo. The end-state answer is data in the mesh: a `Hosting/Deployment`
   record's `pluginRepos[].isRegistrySource`. **Never a list in configuration** — every earlier
   design kept a second copy of that graph and each copy was empty on the deploy that mattered
   (MeshWeaver#2235, Memex#140).
3. **What did it name?** `image` first, `digest` only as a shortcut, `platform_image` for the
   portal to pair with. A wave that names no image at all is the only genuine contract break.
