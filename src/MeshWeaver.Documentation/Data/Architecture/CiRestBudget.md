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
