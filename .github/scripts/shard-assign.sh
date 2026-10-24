#!/usr/bin/env bash
#
# Prints, one per line, the test .csproj paths assigned to shard $SHARD_INDEX of
# $SHARD_TOTAL.
#
# 🚨 Used by BOTH jobs in dotnet-test.yml and they MUST agree:
#   * `build` calls it once per shard to pack that shard's test bins into its OWN
#     artifact (so a shard downloads ~1/SHARD_TOTAL of the output instead of all 3.3 GB);
#   * `test` calls it to decide which projects to run.
# If the two ever disagreed, a shard would run a project whose bin it never
# downloaded. That is why the assignment lives in one file instead of being
# duplicated in two `run:` blocks.
#
# Portable on purpose: no `declare -A`, so it runs on the runner's bash 5 AND on
# a stock macOS bash 3.2 — you can verify a change locally before pushing.
#   SHARD_INDEX=0 SHARD_TOTAL=8 .github/scripts/shard-assign.sh
#
# ── Balancing ────────────────────────────────────────────────────────────────
# Greedy LPT: heaviest first onto the currently least-loaded shard, ties to the
# lowest shard id. Explicit and ORDER-INDEPENDENT — the original
# `counter % SHARD_TOTAL` over a sorted csproj list silently depended on locale
# byte order (C-locale sorted the then-resident MeshWeaver.AI.Test BEFORE
# MeshWeaver.Acme.Test, 'I' 0x49 < 'c' 0x63 — that suite has since moved to
# MeshWeaver.Plugins, #2276), which stacked AI + Orleans — two of the three
# ALC-heaviest projects — onto shard 0 and made it the ~19-minute long pole of
# every run.
#
# Weights are wall-clock SECONDS, the MAX over three full (TEST_SELECTION=all), green
# main runs: 40014951907, 40021939021 and 40027189653, all 2026-10-23. A split
# project's weight is the SUM of its parts' elapsed times. Size for the bad case, not
# the average. The per-project `[CI] <name> exit=<n> elapsed=<s>` markers in the test
# job's log ARE the measurement.
#
# The 2026-10-23 re-measure (the previous table was from 2026-10-08). The table had
# drifted the usual way. It reported loads of ~454 s on every shard, while shard 4
# really ran 875-894 s (Compiler.Pipeline 227→395, Portal.Shared 651→929, FaultInjection
# 61→143). Under the 20-minute job cap that only held while the browser step was quick.
# Whenever its apt mirror stalled, the step spent its whole 480 s budget, and the shard
# was cancelled with every project still progressing. That happened on PR runs
# 40011143666 and 40011313475 and on main run 40027189653, all attempt 1. It was not a
# hang: Memex.Portal.Shared.Test part 3/3 was entering a new class every few seconds
# until the cut, and it measured 315-343 s on the green runs.
# Three things changed:
#   * weights re-measured (above);
#   * splits re-derived from the trx per-class durations. Simulating the every-Nth
#     split of the SORTED class list reproduces the measured parts exactly
#     (Messaging.Hub 106/408, Portal.Shared 315/261/341, Graph 293/250/312):
#       - Memex.Portal.Shared.Test 3→4 parts: 240/179/243/254 s (parts 1/3 and 3/3 were
#         327 and 343 s, past the 288 s headroom threshold below).
#       - MeshWeaver.Graph.Test 3→4: 210/221/252/172 (part 3/3 was 319 s).
#       - MeshWeaver.Messaging.Hub.Test 2→3: 136/223/154. Part 2/2 measured 409-416 s,
#         85-87% of the cap. The guard below did not see it because it divides the
#         total by the part count, and one class (TimeoutMessageNamesTheCallersOwnState-
#         Test, 180 s) dominates that part.
#       - MeshWeaver.Hosting.Test 3→2: 223/226. Its total fell from ~600 to ~450 s, and
#         its 3-part split had become 128/116/206, the unevenness that stacks a shard.
#   * the browser step no longer reaches an Ubuntu mirror. The font set is staged once
#     and restored from a cache (.github/scripts/stage-chromium-fonts.sh), so the browser
#     costs a shard at most 7 min end to end (its steps' `timeout-minutes`), not ~10.
#   * CI_SHARD_TOTAL 6→8 (dotnet-test.yml). Every scheduled project together (unlisted
#     ones at DEFAULT_WEIGHT, #7087's Compiler.Pipeline split included) is ~4,100 s of
#     tests. The job cap leaves a shard ~630 s of tests after the browser's 420 s bound
#     and ~150 s of download and setup. At eight the average is ~513 s, inside the
#     guard's 90% (567 s); at seven it would be ~586 s.
#     ShardWeightHeadroomGuard.TheIdealShardLoadFitsTheShardJobCap checks this arithmetic.
#
# 🚨 The every-Nth split is uneven, and the LPT loop cannot see it. It weights every
# part at total÷N, so two heavy parts can land on one shard. Estimated from the per-part
# figures above, the heaviest of the eight shards runs ~545 s. Re-check with the
# per-part numbers, not with the loads this script prints.
#
# 🚨 RE-MEASURE WHEN A PROJECT GROWS — nothing here does it for you, and a stale
# table is INVISIBLE: the LPT loop still reports six perfectly balanced shards
# (it balances the NUMBERS, not the clock), so the only symptom is a long pole
# nobody attributes to this file. The 2026-08-04 table drifted for three weeks
# and cost ~170 s of long pole on EVERY run: it reported loads of 338/338/337/
# 337/337/337 while the shards actually ran 369/634/536/288/604/357. Two entries
# were most of it — PluginCatalog 30→347 s (it grew from 15 to 82 test files and
# now runs 646 tests) and Monolith 345→662 s. The 2026-07-13 table before it had
# drifted the same way (Monolith 200→345, Data 25→73, PluginTester 60→8).
#
# The floor for ANY sharding is the heaviest single SCHEDULABLE UNIT — a project,
# or one part of a split one. Today that is Hosting.Orleans.Test at 272 s. Adding
# shards below that floor buys nothing.
#
# SPLIT RULE — TWO independent triggers. Either one is sufficient.
#
#  (1) BALANCE. Solo weight exceeds the ideal shard load (sum ÷ SHARD_TOTAL),
#      because only then is the project the binding floor. (AI.Test's split was
#      dropped under this rule before the suite itself moved to
#      MeshWeaver.Plugins, #2276; Monolith's three parts and
#      PluginCatalog's two left with the same migration.)
#
#      Re-measure against a full run before changing a split. A stale low weight
#      puts several heavy projects on one shard while the printed loads look even.
#      On run 37830774943, shard 4's measured tests were 290 + 435 + 38 + the
#      unfinished Compiler.Pipeline suite, against a declared load of 332 s.
#
#  (2) 🚨 HEADROOM AGAINST THE PER-PROJECT CAP (#2747). Solo weight exceeds ~60%
#      of the `timeout 8m` = 480 s wall-clock cap each project runs under in
#      dotnet-test.yml. This trigger is INDEPENDENT of balance and it is the one
#      that bites in production: a project can sit comfortably under the ideal
#      shard load and still be one slow runner away from exit=124.
#
#      PluginCatalog was exactly that. Measured across four consecutive runs it
#      ran 315/320/315 s — 66-72% of the cap — and then 480 s exit=124 TIMEOUT on
#      e12697ebd (run 33302269352, shard 0), with the SAME tree passing at 320 s
#      on the very next run. No change was responsible. The five "test failures"
#      that came with the kill were timing casualties of it, not defects, so every
#      occurrence costs a full investigation before it can be dismissed — and this
#      repo's rules (correctly) forbid simply re-running it.
#
#      Raising the cap is NOT the alternative: the cap is what turns a wedge into a
#      bounded, attributable failure instead of a 20-minute shard. Splitting lowers
#      the numerator instead — PluginCatalog's two parts run ~174 s each, ~36% of
#      the cap — and it costs nothing, because the parts land on different runners.
#
#      Memex.Portal.Shared.Test was the next one: listed at 25 s, it measured 431 s solo on
#      run 36816633012 (90% of the cap) and was killed at 480 s exit=124 on run 36817124160,
#      still progressing through its classes, once #5894 added ~28 s of deliberate waits. It
#      now runs in three parts: the two parts measured 327 and 324 s on
#      37822062147, both beyond the 288 s headroom threshold.
#
#      MeshWeaver.Hosting.Test was the one after that: listed at 27 s, it measured 364-367 s
#      solo on runs 36907697857, 36909173530 and 36909294363 (76% of the cap) and was killed at
#      480 s exit=124 on run 36913173194, still progressing through its classes, once three
#      security tests added ~40 s of deliberate negative waits. It ran in two parts after that,
#      and outgrew them: on main run 38685284606 part 2/2 measured 426 s (89% of the cap) beside
#      part 1/2 at 174 s, and on PR runs 38685234550 and 38685319202 — the same main base, one
#      class added or removed — the every-Nth split flipped parity, part 1/2 drew the 36 s
#      negative-wait classes (BuildTriggeredSyncPinsTheBuiltCommitTest, GreenBuildSkipsASettled-
#      SourceTest, SaveMeshNodeRequestIsACheckedWriteTest, SystemOwnedSweepRetractsTheLastAdminTest)
#      and was killed at 480 s exit=124, still progressing. Measured total ~600 s; it runs in
#      three parts since.
#
#      MeshWeaver.Graph.Test was the one after that: listed at 64 s, it measured 406 s solo on
#      run 37158590539 (85% of the cap) and was killed at 480 s exit=124 on run 37159733607,
#      still progressing through its classes (no TEST START without its TEST END). It runs in
#      two parts since — and outgrew those too: on PR runs 39101376911 and 39125662679 part 1/2
#      hit the 480 s cap exit=124 (the first FINISHED its classes at 480 s, the second was cut
#      starting its last class), while part 2/2 measured 250 s; locally the same half measured
#      304-325 s on main and on the PR alike, so this is growth, not a hang. It runs in three
#      parts since. Messaging.Hub.Test grew from 76 s in the old table to
#      428 s on each of the three green runs and 435 s on 37830774943; it now
#      runs in two parts.
#
#      MeshWeaver.Compiler.Pipeline.Test was the one after that: listed at 227 s, it measured
#      388 s solo on PR run 39964851393 (81% of the cap, already past the 288 s threshold) and was
#      killed at 480 s exit=124 on PR run 39965744393 (#7087), still progressing through its
#      classes, once #7072's regression class added three 36 s deliberate negative waits
#      (`NotEmit(TestTimeouts.Quick)` is 36 s on CI). Measured total ~500 s; it runs in three
#      parts since, so no every-Nth parity can stack its 36 s classes past the threshold.
#
# 🚨 The two triggers must BOTH be checked when re-measuring. A project that grows
# past 288 s (60% of 480) needs splitting even while the LPT loop still reports
# six balanced shards, which is precisely why the balance rule alone missed this.
#
# Unlisted projects get DEFAULT_WEIGHT — a deliberate over-estimate, since an
# unlisted project is a NEW one whose cost nobody has measured yet.
#
# ── Splitting a heavyweight across shards ────────────────────────────────────
# An optional THIRD column splits one project into N parts, each weighted 1/N and
# scheduled independently — so the halves land on DIFFERENT shards, i.e. different
# runners. That matters twice over: it lowers the floor (no amount of sharding can
# beat the heaviest single project), and it introduces no shared-resource contention,
# because two runners share nothing. `maxParallelThreads: 1` is per-process and stays
# intact.
#
# A split entry prints as `<csproj>#<part>/<parts>`; the test job turns that into an
# `-class` filter list by enumerating the assembly's classes and taking every Nth.
# The BUILD job strips the suffix, so a split project's bin is packed into every shard
# that runs one of its parts.
set -euo pipefail

# `--print-weights` dumps the table below and exits, so the test job's drift report can
# compare measured seconds against the SAME table the assignment uses. It deliberately
# runs BEFORE the SHARD_INDEX/SHARD_TOTAL guards — printing the table is not sharding and
# needs neither. 🚨 Never copy the table into a workflow to avoid this flag: two copies is
# exactly how the assignment and the packing could disagree, which is why the whole file
# exists.
DEFAULT_WEIGHT=10

if [ "${1:-}" = "--print-weights" ]; then
  print_weights=1
else
  print_weights=0
  : "${SHARD_INDEX:?SHARD_INDEX must be set}"
  : "${SHARD_TOTAL:?SHARD_TOTAL must be set}"
fi

# "<seconds> <project-name>", heaviest first.
WEIGHTS=$(cat <<'EOF'
929 Memex.Portal.Shared.Test 4
272 MeshWeaver.Hosting.Orleans.Test
142 MeshWeaver.Data.Test
522 MeshWeaver.Messaging.Hub.Test 3
51 MeshWeaver.PluginTester.Test
861 MeshWeaver.Graph.Test 4
143 MeshWeaver.FaultInjection.Test
25 MeshWeaver.Updates.Test
453 MeshWeaver.Hosting.Test 2
68 MeshWeaver.Layout.Test
7 MeshWeaver.ContentCollections.Test
37 MeshWeaver.Documentation.Test
500 MeshWeaver.Compiler.Pipeline.Test 3
2 MeshWeaver.ContentCollections.Indexing.Test
2 MeshWeaver.Markdown.Collaboration.Test
2 MeshWeaver.Portal.E2E.Test
1 MeshWeaver.Data.TestDomain
1 MeshWeaver.Testing.Xunit.Test
1 MeshWeaver.Testing.Xunit
EOF
)

if [ "$print_weights" = "1" ]; then
  printf '%s\n' "$WEIGHTS"
  exit 0
fi

weights_file=$(mktemp)
trap 'rm -f "$weights_file"' EXIT
printf '%s\n' "$WEIGHTS" > "$weights_file"

# EVERY test project runs — there is no exclusion list. PostgreSql and Cosmos come up via
# Testcontainers on the pre-installed Docker, Orleans via in-proc TestCluster, Acme/FutuRe via
# dynamic Code-piece compilation.
#
# Cosmos was excluded until the emulator was verified on a runner: the CLASSIC
# azure-cosmos-emulator image really is too heavy (multi-GB, minutes to start, HTTPS-only, and it
# publishes a linux/amd64 manifest ONLY, so it cannot run on an arm64 dev machine at all). The
# vnext-preview image CosmosFixture pins is ~0.68 GB, reports ready in ~10 s over plain HTTP, and
# ships arm64 — so the project now runs like any other, and green-SKIPS (never reds) when Docker
# is unavailable.
#
# The weights arrive as a FILE, not `awk -v` — macOS's awk rejects a -v value
# containing newlines ("newline in string"), so the -v form silently worked on
# the runner while failing for anyone verifying a change locally.
# ── The change set's selection (affected-tests.py, 2026-09-13) ──────────────────────────
# TEST_SELECTION=all (or unset) keeps every project; =incremental keeps the names listed in
# TEST_PROJECTS (comma-separated); =none keeps nothing — the caller decides what an empty shard
# means (a pull request whose change no test project reads). The SAME env reaches the build's
# packaging and every test shard, so the two assignments stay identical.
select_projects() {
  case "${TEST_SELECTION:-all}" in
    none)
      echo "shard-assign: TEST_SELECTION=none — no project is owed" >&2
      # 🚨 DRAIN stdin, never just return. A branch that reads nothing closes the pipe while
      # `find` is still writing into it → `find` takes SIGPIPE and exits 141 ("find: write
      # error", "Broken pipe"), and `set -o pipefail` above turns that into the whole script
      # failing. Measured 2026-09-20 on MeshWeaver#4979: `none` is reached only by a change set
      # no test project reads (a docs- or skill-only pull request), so the REQUIRED
      # `Build solution (once)` job died with a bare `Process completed with exit code 1`
      # printed four lines AFTER `Build succeeded. 0 Warning(s) 0 Error(s)` — the one shape
      # that reads as "the build broke" when the build was fine and nothing was owed.
      cat > /dev/null ;;
    incremental)
      awk -v keep=",${TEST_PROJECTS:-}," '{ n=$0; sub(/.*\//,"",n); sub(/\.csproj$/,"",n); if (index(keep, "," n ",")) print }' ;;
    *) cat ;;
  esac
}
find test -name '*.csproj' ! -path '*/bin/*' \
  | select_projects \
  | awk -v dflt="$DEFAULT_WEIGHT" '
      FNR == NR { if (NF >= 2) { weight[$2] = $1; if (NF >= 3) parts[$2] = $3 } next }
      {
        name = $0; sub(/.*\//, "", name); sub(/\.csproj$/, "", name)
        w = (name in weight ? weight[name] : dflt)
        n = (name in parts ? parts[name] : 1)
        if (n <= 1) { printf "%06d %s %s\n", w, name, $0; next }
        # Each part is its own schedulable unit at 1/N the weight.
        for (i = 1; i <= n; i++)
          printf "%06d %s#%d/%d %s#%d/%d\n", int(w / n), name, i, n, $0, i, n
      }' "$weights_file" - \
  | sort -k1,1nr -k2,2 \
  | awk -v idx="$SHARD_INDEX" -v total="$SHARD_TOTAL" '
      BEGIN { for (i = 0; i < total; i++) loads[i] = 0 }
      {
        best = 0
        for (i = 1; i < total; i++) if (loads[i] < loads[best]) best = i
        loads[best] += $1 + 0
        if (best == idx) print $3
      }
      END {
        # Diagnostics on stderr — stdout is the machine-readable csproj list.
        s = ""
        for (i = 0; i < total; i++) s = s " " loads[i]
        printf "shard-assign: shard %s/%s, weighted loads:%s\n", idx, total, s > "/dev/stderr"
      }'
