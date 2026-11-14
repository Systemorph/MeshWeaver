---
Name: CD Delivery Lines
Category: Architecture
Description: How one main-cd.yml delivers several branch lines — `main` and `main3` — each with its own copy of the file, its own Plugins branch, its own floating label, its own concurrency lanes and its own major for every "newest set" selection.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3v18"/><path d="M18 3v18"/><path d="M6 8h12"/><path d="M6 16h12"/></svg>
---

# CD Delivery Lines

Policy `major-lines-run-separately` makes a platform major a **line**: a branch that builds,
tests and delivers on its own. `main` carries one major and `main3` the previous one. This page
describes how core's delivery pipeline (`.github/workflows/main-cd.yml`) serves every line from
ONE file. The same commit applies to every line's branch unchanged, because the line is derived
from the branch and never written into the file.

## The declaration

`.github/delivery-lines.json` declares the lines and the repository's default branch:

| field | meaning |
|---|---|
| `branch` | the branch that IS the line |
| `pluginsRef` | the MeshWeaver.Plugins branch the line's portal host is built from (`main` → `main`, `main3` → `main3`); `vars.MW_PLUGINS_REF` still overrides it per run, for EVERY line at once. **Bootstrap of a new major:** the first set of a new major is built against the Plugins branch that carries that major (for 4.0, `feat/net11-v4`), because Plugins `main` still carries the previous one. Only the `main` entry names it, so the previous line's entry, and with it that line's CD, stay untouched. The change that follows the Plugins merge flips it back to `main`. Never use `vars.MW_PLUGINS_REF` for a bootstrap: it would point the previous line at the new major too. |
| `satelliteRef` | the satellite branch `satellite-compat` compiles against the line's set (`main` → `main`, `main3` → `main3`: every satellite in the `satellite-compat` register carries a protected `main3`, protected exactly like its `main`) |
| `label` | the line's floating image label: the newest PROMOTED set of the line (`main`, `main3`), declared in `.github/release-channels.json` with `"line"` |
| `movesLatest` | whether the line moves the line-less `latest` labels. Exactly one line does |

## Deriving the line

The `line` job runs first. It calls `.github/scripts/cd-line.py derive`, which maps the event to a
line:

| event | line | copy of main-cd.yml that runs |
|---|---|---|
| `workflow_run` (Build-and-Test finished on a push) | the triggering branch, which must be the default branch | the default branch's |
| `schedule` | the default branch | the default branch's |
| `push` | the pushed branch, which must be a NON-default line | that branch's |
| `workflow_dispatch` | the dispatched ref | the one at that ref |

Anything else is **refused RED by name**: an undeclared branch, a `workflow_run` for another
branch (it would run the default line's file for that line's commit), a push to the default
branch (it would race the tests), or a payload whose default branch disagrees with the
declaration. There is no default line. The same job reads the line's platform **major** from
`PlatformVersion` in the line's `Directory.Build.props` (`cd-line.py platform-major`).

## Why the triggers are shaped this way

`workflow_run` and `schedule` always execute the default branch's copy of the file. Once the
lines' trees diverge, that copy must never decide anything about another line's commit. So:

- `workflow_run` stays `branches: [main]`, the default line only.
- Every non-default line has a `push` trigger. A push lands before Build-and-Test has a verdict,
  so that run is a **reconcile** of the line: it resolves the line's tip through the API and ships
  the newest commit CI vouched for, or nothing.
- The default branch's hourly tick runs `reconcile-lines`, which dispatches `main-cd.yml` **at**
  each other line's branch. A dispatch runs the copy at its ref, so main3's own file reconciles
  main3. The job is RED, and dispatches nothing, when a declared line has no branch, or when its
  branch does not carry `.github/delivery-lines.json`. Such a branch still holds the single-line
  CD, which would resolve `commits/main` and publish main's tip from it.
- Build-and-Test (`dotnet-test.yml`) runs on a push to every line, with a per-commit concurrency
  group and no cancellation, exactly as on `main`. CD ships only a commit whose required check
  is green, and only a push run puts that check on a landed commit.

## What reads the line

| where | what changed |
|---|---|
| `gate` | resolves `commits/<line>` and `commits?sha=<line>`, the line's Plugins branch, and its satellite branch. The `line` outputs pass through as `needs.gate.outputs.*` |
| `promote` phase B | moves `<label>` on the set's repositories. `latest` moves only for the `movesLatest` line. The never-backwards check ignores the run numbers of the OTHER lines' CD runs, because every line's sets share one run counter |
| `control-promote` | `memex-control:<label>` |
| `verify-images` | `check-image-set.sh --line-label <label> --moves-latest <bool>`. Both are required with `--pointers` |
| the satellite-compat baseline | the set `mw-plugin-test:<label>` names |
| `arm` | `arm-promoted-set.py select --branch <line> --major <major>` and `armed-state --major <major>`. Phase D's never-backwards check compares within the major, and `latest` moves only for the `movesLatest` line. The job's concurrency group is `main-cd-arm-<major>`, because the cursor it protects is per major |
| the promotion record | carries `line`; its base is the newest armed set of the line's major |
| `notify-platform-update` | the "still the newest armed set?" re-check reads within the major |
| `delivery-verdict` | resolves the line's tip, and its hand-over dispatches `--ref <line>` |

**Cold start of a major.** When a line moves to a new major (main to 4.x), no set of that major has
been armed yet, while the fleet is armed on the previous one. `armed-state --major 4` then answers
a cursor of 0 for major 4 (`cold_start: true`, nothing to resume), and `select` arms the newest 4.x
set whose platform verdict is green through the ordinary path, without `--override`. A registry
with no armed set of ANY major is still refused: that is a broken read, never "nothing armed".

**Who may move `latest`.** In `.github/release-channels.json` every main-cd.yml binding of `latest`
carries `onlyTheLineThatMovesLatest: true`, and `release-channels.py check` refuses one without
it. `tags_for` therefore hands `latest` only to the line that moves it. Phase B is executed in
`test-cd-steps.py` (step `pointers_b`) for both lines, with a mutation control: a `main3` promote
writes `main3` on every repository and `latest` nowhere.

The workflow-level concurrency group is per line: the push lane is `github.ref`, and the reconcile
lane is `reconcile-<ref name>` (`main-cd-reconcile-main`, `main-cd-reconcile-main3`).
`MainRunsAreNeverCancelledGuard` evaluates the expression for both lines.

## The resolver serves ONE line

`.github/scripts/resolve-platform.py` picks the platform set a dependent builds against. Every
dependent fetches it from core at a ref and runs it there, so the same bytes ship on every line's
branch, and the line is declared by the caller rather than written into the file. It lists
`main-cd.yml` runs only on the line it serves, so a v3 dependent resolves `3.1.N` sets and a v4
dependent `4.0.N` sets. Until this was fixed, every listing spelled `branch=main`: the `main3` copy
resolved the v4 set `4.0.13205` for v3 code, and a freeze of the `main3` set `3.1.13192` matched no
run at all.

The line comes from the first of these that is present:

| source | when |
|---|---|
| `--line <branch>` | the caller declares it |
| `MW_CORE_LINE` | the same declaration through the environment |
| `--scripts-ref <ref>` | the ref the copy was fetched at, when that ref is a line branch. `node-repo-gate.yml` (`plan` and each shard's refresh) and `node-repo-publish-bake.yml` pass their `scripts-ref`, so a caller at `scripts-ref: main3` serves `main3` |
| none of these | the declared default line (`defaultBranch`), with a `::warning` saying the line was not declared |

A declared value that is not a line (`--line main4`) fails red and names every declared line. A
`scripts-ref` that is a commit sha or a tag says nothing about the line, so it falls through to the
default. The log and the step summary always print `core line: <line> — <how it was decided>`.

- **The newest sealed set** is chosen from the served line's runs only. The stale-listing checks
  (page 1, the recent-run witness, the re-reads) read the same line.
- **A freeze** names one set. It is searched on the served line first, then on every other
  declared line. Run numbers are unique across lines, so the match is unambiguous, and a set
  freeze stops scanning a line once the listing passes below its run number. A match on another
  line is taken, because a freeze is an instruction, and the run carries a `Freeze on another core
  line` warning. A freeze that matches nothing fails red and names every line it searched.
- **The `--passed-on-main` ceiling** reads the dependent's own `ci.yml` runs on the served line's
  declared `satelliteRef` (`main3` for the v3 line), or on `--passed-on-branch` when given.

The script carries a mirror of `.github/delivery-lines.json` (`DELIVERY_LINES`, `DEFAULT_LINE`),
because a fetched copy has no sibling files. `--self-test` compares the mirror with the JSON
whenever the JSON sits beside the script in a core tree, so a line added in one place only fails
the self-test. The self-test also covers both lines, the freeze search and the ceiling branch, and
includes a negative control that puts the hard-coded `branch=main` listing back and shows that the
line cases fail.

**What a dependent must still do.** A satellite's own `platform-ref` job fetches the resolver at
`main3` and runs it without `--scripts-ref`, so it gets the default line and the warning. Each such
job passes `--line main3` (or `--scripts-ref "$SCRIPTS_REF"`) in its own repository.

## Still one fleet state

These places still announce or decide ONE fleet-wide state. They receive the line but do not key
on it yet. The per-major channels (`latest3`, `latest4`) are a separate change:

- `control-first` (group `main-cd-control-first`): one control instance. It tags
  `memex-control:<version>` and its line pointers, and announces `self-update-available` for the
  `control` record. Both lines give control their builds.
- The `ci-failure` / `cd-unhealed` heal ledger and the `fleet-unarmed` alert: one issue each, for
  both lines.
- The build-host hand-over lives in MeshWeaver.Deploy. It reads the sealed sets from the registry,
  and core hands it the right set by keeping arming within the major.

## The guard

`cd-line.py check-workflow` runs on every pull request. It fails when main-cd.yml spells a
`main` that means "the line" in code: a quoted `'main'`, `commits/main`, `?sha=main`,
`--ref main`, `refs/heads/main`, an image tag `:main`, `ref: main`, a tag loop over `main`, or a
`branches:` filter. Comments and message prose are not scanned. The one allowed spelling is
`on.workflow_run.branches: [main]`, and the guard holds it equal to the declared default branch.
The guard also checks that main-cd's push trigger lists exactly the non-default lines, that
Build-and-Test pushes on every line, and that `reconcile-lines` exists.

See also [Release Channels](../ReleaseChannels).
