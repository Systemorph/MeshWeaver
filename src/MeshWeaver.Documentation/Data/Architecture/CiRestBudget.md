---
Name: The CI REST Budget
Category: Architecture
Description: Every REST call a satellite's CI makes with `github.token` is charged to ONE per-repository budget — 1,000 requests an hour on the organisation's plan — and when it runs out every job of every pull request goes red on "API rate limit exceeded for installation" while fetching a file. What spent it (measured on MeshWeaver.Plugins), the two consumers that were fixed at the root, the instrument every job that reads a platform file now prints, and what was not measured.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3a9 9 0 1 0 9 9"/><path d="M12 7v5l3 3"/><path d="M16 3h5v5"/></svg>
---

# The CI REST Budget

**A satellite's CI spends one shared budget.** Every `gh api`, every REST `curl`, every
`urllib` call a job makes with `${{ github.token }}` is charged to the CALLING repository's
`GITHUB_TOKEN` budget. GitHub documents it as **1,000 requests per hour per repository** (15,000
only for Enterprise Cloud; this organisation is on the Team plan). Every workflow of the repository
draws on it — the pull-request CI, the stage-advance listener, the retry steward, the arm and
supersede helpers — and the reusable `node-repo-*` lanes core ships draw on the CALLER's budget,
not core's, because they run with the caller's token.

When it runs out, the refusal is the same for every call — *"API rate limit exceeded for
installation"* — and it lands wherever the next call happens to be. On MeshWeaver.Plugins on
2026-10-07 11:18–11:25Z that was: the closing-keyword gate, the content-CI path guard and the
manifest checker failing to *fetch their script*; the stage gate holding a run as "unreadable";
`platform-requirement.py` dying on an `HTTPError 403`; and the runner watchdog polling the jobs
listing every 20 s through the whole window, reading a refusal each time. None of those is the
defect. The defect is what spent the budget.

## What spent it — measured on MeshWeaver.Plugins, 2026-10-07 10:20–11:20Z

The hour before the window, read from the Actions runs listing (REST, read-only):

| workflow | runs in the hour | REST calls per run (floor) |
|---|---|---|
| Plugin Catalog CI (`pull_request` 33, `push` 3) | 36, **1,790 job executions** incl. re-run attempts | **20–46 platform-file fetches counted** (a floor) + the stage gate's 6 + resolver/requirement scripts |
| Stage advance (`pull_request_review_comment` 60, `pull_request_review` 39, other 5) | 104 | **~10** (9 counted with a shim around `gh`, plus the script fetch) |
| Retry known transients / Merged tree current | 30 / 33 | 0 when skipped; jobs + annotations listings on a failure |
| Arm auto-merge, Paired core change, Supersede | 29 / 33 / 16 | 0–3 |

The 2026-10-05 22:20–23:20Z window had the same shape: 39 CI pull-request runs, 52 + 40 stage-advance
review events, 45 retry runs. (The 2026-10-06 ~21:45Z refusals in #6215 / #6216 are a DIFFERENT
budget — the `systemorph-com` App installation the portal's PR steward uses; Plugins CI was quiet
then, 80 runs in the hour.)

How the per-run numbers were taken:

- **Platform-file fetches.** Every job log of one run per workflow kind was downloaded and the
  executed step scripts (the `##[group]Run …` blocks — a skipped step prints none) were scanned
  for REST call sites. Full PR runs carried 36–46 core contents-API fetches (`46 of 48 jobs`,
  `36 of 46`, `45 of 77` on a push run, `20 of 35` on a held run). That is a FLOOR: a loop counts
  once, and the validate lane alone loops over 11 lane files plus two scripts — ~31 fetches in one
  job. Over 1,790 job executions that is **~1,000–2,000 calls an hour from file fetches alone**,
  already more than the whole budget.
- **Stage advance.** `check-review-answered.py --stage-advance --pr N` and `--stage-gate` were run
  locally with a `gh` shim that logs each call and refuses any write: 9 and 6 calls. Every person's
  reply to a finding raised **both** `pull_request_review_comment` and `pull_request_review`, so the
  listener ran twice for one change of state — 60 of 60 comment events in the hour had a
  same-branch review event within 15 s (52 of 52 on 10-05).

## The fixes, at the root

**1. The lanes read the platform's files over git, never through REST.** Core is a PUBLIC
repository, so the same bytes are readable over anonymous git, which spends no REST budget.
`.github/actions/core-file` installs `$RUNNER_TEMP/mw-core-file` (source:
`.github/scripts/mw-core-file.sh`) as the first step of every job that reads a platform file, and
every read is

```bash
"$RUNNER_TEMP/mw-core-file" "${SCRIPTS_REF}" ".github/scripts/check-covers.py" > "$RUNNER_TEMP/check-covers.py" \
  || { echo "::error::could not fetch …"; exit 1; }
```

One shallow, blob-less fetch per (job, ref); every later read in the job is local, the blobs are
fetched on demand. Two properties the REST reads did not have: **one commit per job** (a moving
`main` can no longer hand two steps of one job two different trees), and **an abbreviated sha is
refused by shape** (7–39 hex, before any fetch) rather than resolved — git would otherwise take a
same-named branch or tag for it; a hash-like NAMED ref is spelled `refs/heads/…` / `refs/tags/…`. `CiRestBudgetGuard` holds every
`node-repo-*.yml` at zero contents-API reads, requires the install before the first call in each
job, and drives the reader against a local repository (branch, tag, full sha, a moving branch, an
abbreviated sha even with a same-named branch present, a missing path) and the budget mode against
a stubbed `curl` (a normal reading, the 80 % warning, an unanswered request); its negative control
proves both detectors fire on the old shapes.

**A refused read is named as infrastructure, never as an old pin (#6633).** The reader exits `1`
when the ref cannot be FETCHED, and its line says which of two causes it was. When the server
answers that it has no such ref (git's `couldn't find remote ref` / `not our ref`: a mistyped or
deleted branch or tag, a full sha that is not in the repository) it says `REF NOT FOUND — … This IS
the caller's pin`. Every other fetch failure (a network failure, GitHub refusing or throttling the
read) says `INFRASTRUCTURE — … not a fault in the caller's code or its pin`. It exits `3` only when
the ref WAS fetched and the path is absent at it — the one case a lane may read as "a pin older than
the file". The lanes' own `::error::` lines no longer guess: every caller in the lane workflows
points at the reader's line. Before this, a throttled read surfaced as *"a pin older than core
#3761"* and PR owners chased a code fault that did not exist. `PlatformFileReaderGuard` asserts all
three outcomes, and that neither the absent path nor the absent ref is ever called infrastructure.

**2. The stage-advance listener answers `pull_request_review` only.** The comment trigger was a
pure duplicate (above); dropping it removes ~60 of ~100 runs an hour at ~10 calls each. The caller
owns its triggers (`stage-advance.yml` in each satellite); the lane's header names the rule.

## The instrument

The install step prints the token's budget once per job — `GET /rate_limit`, which GitHub does not
count against the budget:

```text
REST budget of this job's token: 412 of 1000 used, resets 11:42:07Z
```

and turns it into a `::warning title=REST budget nearly spent::` at 80 %. A saturated hour is
therefore visible in the log of every job that reads a platform file BEFORE it reds anything, with
the reset time beside it. (A job that reads no platform file does not install the reader and prints
no reading.)

## An empty budget is NO VERDICT, never a finding

**The 2026-10-27 recurrence was core's own.** From 20:37 to 21:14Z every core and Plugins pull-request
run went red on *"API rate limit exceeded for installation"* (HTTP 403) — and the gates did not say
so. They turned the refusal into a verdict about the pull request:

- `scripts/read-current-pr-body.py` printed *"Could not read the current PR body"*, and the
  declaration gates (Closing keywords, Cross-repo pair, Interface additions, Public surface,
  Satellite package pins, i18n mirror) failed as if the body were wrong;
- `.github/scripts/fetch-deployed-plugin-set.sh` ran every lookup as `gh api … || continue`, so each
  refused lookup read as "this run is not a candidate", and the ladder concluded *"the deployed plugin
  set is not available"* over a set that existed.

Three changes, all in core:

1. **A rate limit, a 5xx or a dropped connection is retried, then reported as NO VERDICT.** Both
   scripts share one REST reader (`api_get` in `read-current-pr-body.py`; shell scripts call it
   through `.github/scripts/github-api-get.py`). It recognises a rate limit by its status (403/429)
   together with `x-ratelimit-remaining: 0`, a `retry-after`, or "rate limit" in the message; it
   waits as long as `retry-after` / `x-ratelimit-reset` say, within a budget that is far below the
   job's timeout; and a wait the budget cannot cover is not slept at all. What it prints then is

   ```text
   NO VERDICT — GitHub API rate-limited until 2026-10-27T21:14:00Z; re-run after it.
   ```

   and the step is red (exit 3). Only a genuine **404** lets `fetch-deployed-plugin-set.sh` skip a
   candidate; any other refusal (401, a non-rate-limit 403) is red at once, without retries. The
   error BODY is read over the same connection as the request, so that read can fail too (a reset
   or a timeout on a 503): an unreadable body on a 5xx, 429 or 403 is retried like the outage it is
   and ends as NO VERDICT, never a traceback — a 403 because its body is part of what tells a rate
   limit from a refusal — while a 401 or 404 stays an immediate refusal whatever its body (#7457). The
   closing-keyword gate's label probe says the same sentence instead of *"the token cannot read
   issues"*. Both scripts carry self-tests against a fake API (a 403 rate limit → NO VERDICT, a 404 →
   skip, a 200 → the normal verdict) that run before the real call.
2. **The pull-request body is read ONCE per run attempt, on an App's budget.** The `pr-body` job in
   `dotnet-test.yml` reads it with a `fleet-reader` installation token, which has its own, larger
   bucket than the repository's `GITHUB_TOKEN` (see [GitHub App Credentials](../GitHubAppCredentials)),
   and hands it to the five declaration gates as an output. A gate reuses that body only when it was
   read in the SAME run attempt for the SAME head. "Re-run failed jobs" keeps the earlier attempt's
   outputs, so a gate re-run after the author corrected the body reads the CURRENT body itself, as
   before. A fork pull request gets no secret, so it reads with the run's own token. That exemption
   is written once, on the event (`head.repo.full_name == github.repository`). Every
   same-repository pull request, Dependabot's included (both secret stores hold the App), asserts the
   credential and is red naming it when it is missing. A failed `pr-body` holds stage 0 and fails
   `Consolidate test results` with its own line.
3. **Fewer calls.** Five body reads became one, and `fetch-deployed-plugin-set.sh` no longer asks for
   the chosen run a second time: its head sha is taken from the lookup that already read the run.
   Every listing asks for `per_page=100`.

4. **Every other caller that asked GitHub on its own now asks through the same reader.** The two
   scripts above were the ones the outage was first seen in; the same shape sat in five more places,
   each turning a rate-limit 403 into a sentence that read like a provisioning defect (#7448, #7416):

   | Caller | It used to say on a rate-limit 403 | Now |
   |---|---|---|
   | The fleet-token probes of `shared-rules` and `cross-repo-pair` (`dotnet-test.yml`) and the `shared-rules.yml` sweep — three copies of a `curl -w '%{http_code}'` loop | *"the token is valid but lacks contents:read here, or is rate-limited"* | `.github/scripts/probe-fleet-read.py`: NO VERDICT, exit 3, for an outage; a plain 401/403/404 stays a finding, exit 1; a finding beats an outage |
   | `check-cross-repo-pair.py` resolving a `Pairs-with:` counterpart | *"lacks pull-requests:read here, or is rate-limited"* | `PairResolutionError` carrying the NO VERDICT sentence |
   | `transitional_allow.py` (record-signature, type-forward and parser-delta allow entries) | the same | `AllowResolutionError` carrying the NO VERDICT sentence — still never an allowance |
   | `check-shared-rules.py` reading each repo's `AGENTS.md` | *"lacks contents:read on this repo (or is rate-limited)"* | `Problem` carrying the NO VERDICT sentence (`api_get(raw=True)` for the raw file) |
   | `ci-failure-ledger.py`, the ci-failure ledger every scheduled lane reports through | *"the CALLER job must grant `actions: read` …"* — on run 40598141020 (the merge push of #7422), whose caller had granted it | waits for the reset inside `MW_API_OUTAGE_WAIT_MINUTES` (22 in the ledger job, which is capped at 30; 3 in a caller's 5-minute `cause` job), then exits 3 with NO VERDICT. A 5xx on a WRITE is not replayed, because it may have filed the issue. |

   The ledger is fetched ALONE at a scripts ref, so it carries its own copy of the classification
   (`classify_answer`). Everything else imports `api_get`, so there is one rule to change. Each site
   has a test against a fake transport that fails on the old code: `scripts/test-gate-api-outage.py`,
   `probe-fleet-read.py --self-test` and case 20 of `ci-failure-ledger.py --self-test`. A plain
   permission 403 is their negative control and must still name the grant.

**What still spends `GITHUB_TOKEN`:** the ladder's artifact reads (`fleet-reader` has no Actions
permission, and `meshweaver-cloud` is reserved for writes), the closing-keyword gate's issue and label
reads (no read-only App holds Issues), and the Public-surface gate's own body read. That gate also runs
on `merge_group`, where `pr-body` does not run. All three now answer a rate limit with NO VERDICT.

## What was NOT measured

- **The limit itself was not read.** No job in the window printed `/rate_limit`; 1,000/hour is
  GitHub's documented figure for the plan. The new instrument reads it on every job that installs the
  reader from now on.
- **Calls inside Python scripts other than the review predicate** (`resolve-platform.py`,
  `platform-requirement.py`, `retry-known-transients.py`, `module-publication.py`) were not counted,
  and loops were counted once. The per-run figures are floors.
- **Anonymous git from the self-hosted scale sets' shared egress** has no documented hourly cap; it
  is not a REST budget, but it was not load-tested at ~1,800 fetches an hour.
- **Satellites other than MeshWeaver.Plugins** inherit fix 1 through the lanes on their next run;
  fix 2 is a caller change each satellite's `stage-advance.yml` still has to make.
