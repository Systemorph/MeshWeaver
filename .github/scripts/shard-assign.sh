#!/usr/bin/env bash
#
# Prints, one per line, the test .csproj paths assigned to shard $SHARD_INDEX of
# $SHARD_TOTAL.
#
# 🚨 Used by BOTH jobs in dotnet-test.yml and they MUST agree:
#   * `build` calls it once per shard to pack that shard's test bins into its OWN
#     artifact (so a shard downloads ~1/6 of the output instead of all 3.3 GB);
#   * `test` calls it to decide which projects to run.
# If the two ever disagreed, a shard would run a project whose bin it never
# downloaded. That is why the assignment lives in one file instead of being
# duplicated in two `run:` blocks.
#
# Portable on purpose: no `declare -A`, so it runs on the runner's bash 5 AND on
# a stock macOS bash 3.2 — you can verify a change locally before pushing.
#   SHARD_INDEX=0 SHARD_TOTAL=6 .github/scripts/shard-assign.sh
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
# Weights are wall-clock SECONDS, the MAX over three green runs (32917064578,
# 32914770924 and 32912903460, all 2026-08-26) so a shard is sized for the bad
# case, not the average. The per-project `[CI] <name> exit=` markers in the test
# job's log ARE the measurement: take the delta between each `::group::<name>`
# and its marker.
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
# or one part of a split one. Today that is Hosting.Orleans.Test at 269 s, which
# became the long pole when 17 suites moved to MeshWeaver.Plugins (#2276) and took
# Hosting.Monolith.Test (663 s / 3 parts) and PluginCatalog.Test (348 s / 2) with
# them. Adding shards below that floor buys nothing.
#
# SPLIT RULE — TWO independent triggers. Either one is sufficient.
#
#  (1) BALANCE. Solo weight exceeds the ideal shard load (sum ÷ SHARD_TOTAL,
#      currently 731 ÷ 6 ≈ 122 s), because only then is the project the binding
#      floor. (AI.Test's split was dropped under this rule before the suite itself
#      moved to MeshWeaver.Plugins, #2276; Monolith's three parts and
#      PluginCatalog's two left with the same migration.)
#
#      🚨 The remaining table is small enough that this trigger now fires on the
#      four heaviest entries at once. It is NOT an instruction to split them: the
#      table is a stale measurement of a tree that just lost two thirds of its
#      weight, so RE-MEASURE against a real run before acting on it. Trigger (2)
#      is the one with teeth, and nothing currently breaches it.
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
269 MeshWeaver.Hosting.Orleans.Test
91 MeshWeaver.Data.Test
76 MeshWeaver.Messaging.Hub.Test
74 MeshWeaver.PluginTester.Test
64 MeshWeaver.Graph.Test
26 MeshWeaver.Layout.Test
25 Memex.Portal.Shared.Test
8 MeshWeaver.Hosting.Test
7 MeshWeaver.ContentCollections.Test
6 MeshWeaver.Documentation.Test
5 MeshWeaver.Compiler.Pipeline.Test
2 MeshWeaver.ContentCollections.Indexing.Test
2 MeshWeaver.Markdown.Collaboration.Test
2 MeshWeaver.Portal.E2E.Test
1 MeshWeaver.Data.TestDomain
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
find test -name '*.csproj' ! -path '*/bin/*' \
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
