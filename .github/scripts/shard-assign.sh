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
# byte order (C-locale sorts MeshWeaver.AI.Test BEFORE MeshWeaver.Acme.Test,
# 'I' 0x49 < 'c' 0x63), which stacked AI + Orleans — two of the three
# ALC-heaviest projects — onto shard 0 and made it the ~19-minute long pole of
# every run.
#
# Weights are wall-clock SECONDS. The per-project `[CI] <name> exit=` markers in
# the test job's log ARE the measurement: take the delta between successive
# markers (the first project's start is the `Run Tests` step's own start_at).
#
# 🚨 Re-measure when a project's runtime changes materially. This table has now
# drifted twice, and BOTH times the cost was paid on the long pole:
#   * 2026-07-13 → 2026-08-04: Monolith 200→345, Data 25→73, Persistence 165→97.
#   * 2026-08-04 → 2026-08-09: PluginCatalog 30→87 (+190%), PostgreSql 130→161,
#     AI 263→177, Monolith 345→270, Content 91→74. The under-weighted entries are
#     what hurt: shard 1 ran 7.4 min against a 5.4 min ideal, i.e. 2.0 min of pure
#     imbalance on EVERY run, while shard 3 idled at 5.2.
#
# Current table: measured from run 31311680838 (2026-08-09). Simulating the LPT
# below against those weights gives max 5.57 / min 5.42 min — a 0.15 min spread.
#
# ⚠️ Unlike the previous table this is ONE run, not the max over two: the second
# green run's logs no longer carried the `[CI]` markers when this was measured, and
# a fresh single measurement beats a stale two-run max by a wide margin. LPT is
# robust to modest weight error — the failure mode it cannot absorb is a 190%
# under-estimate like PluginCatalog's. Take the max over two runs at the next
# re-measure.
#
# The floor for ANY sharding is the heaviest single SCHEDULABLE unit. Today that is
# Hosting.Monolith.Test at 270 s — but it is split 2, so the real floor is ~135 s.
# Adding shards below that buys nothing.
#
# Total measured work is ~32.5 min across 63 projects, so the ideal per-shard load is
# 5.4 min at 6 shards, 8.1 at 4, 10.8 at 3. Cutting shard count therefore makes the
# critical path WORSE while tests remain single-threaded — it only becomes attractive
# once intra-project parallelism lands (see test/Directory.Build.props), because that
# shrinks the 32.5 min, not the shard count.
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

: "${SHARD_INDEX:?SHARD_INDEX must be set}"
: "${SHARD_TOTAL:?SHARD_TOTAL must be set}"

DEFAULT_WEIGHT=10

# "<seconds> <project-name>", heaviest first.
WEIGHTS=$(cat <<'EOF'
270 MeshWeaver.Hosting.Monolith.Test 2
214 MeshWeaver.Hosting.Orleans.Test
177 MeshWeaver.AI.Test 2
161 MeshWeaver.Hosting.PostgreSql.Test
103 MeshWeaver.Threading.Test
87 MeshWeaver.PluginCatalog.Test
74 MeshWeaver.Content.Test
73 MeshWeaver.Persistence.Test
68 MeshWeaver.FutuRe.Test
66 MeshWeaver.Data.Test
62 MeshWeaver.GitSync.Test
60 MeshWeaver.Security.Test
59 MeshWeaver.Acme.Test
57 MeshWeaver.Query.Test
41 MeshWeaver.Auth.Test
40 MeshWeaver.Autocomplete.Test
33 MeshWeaver.Graph.Test
27 MeshWeaver.NodeOperations.Test
26 MeshWeaver.Hosting.Cosmos.Test
25 MeshWeaver.InstanceSync.Test
22 MeshWeaver.Messaging.Hub.Test
20 MeshWeaver.Import.Test
20 MeshWeaver.Layout.Test
17 MeshWeaver.Courses.Test
15 MeshWeaver.Markdown.Test
14 MeshWeaver.MemexTemplate.Test
12 MeshWeaver.PathResolution.Test
9 MeshWeaver.ContentCollections.Indexing.Graph.Test
9 MeshWeaver.Northwind.Test
8 MeshWeaver.Hosting.Blazor.Test
8 MeshWeaver.PluginTester.Test
8 MeshWeaver.PythonDemo.Test
6 MeshWeaver.AccessControl.Test
6 MeshWeaver.ContentCollections.Indexing.PostgreSql.Test
6 MeshWeaver.Hosting.Grpc.Test
5 MeshWeaver.ContentCollections.Test
5 MeshWeaver.Todo.Test
4 Memex.Portal.Shared.Test
4 MeshWeaver.Hosting.Test
4 MeshWeaver.MathDemo.Test
3 MeshWeaver.Hosting.Sqlite.Test
3 MeshWeaver.Observability.Test
3 MeshWeaver.Serialization.Test
3 MeshWeaver.Social.Test
2 MeshWeaver.Documentation.Test
2 MeshWeaver.Maui.Integration.Test
1 MeshWeaver.AI.Test.FakeCli
1 MeshWeaver.Connection.SignalR.Test
1 MeshWeaver.ContentCollections.Indexing.Test
1 MeshWeaver.DataSetReader.Test
1 MeshWeaver.Hosting.Snowflake.Test
1 MeshWeaver.Markdown.Collaboration.Test
1 MeshWeaver.Markdown.Export.Test
1 MeshWeaver.Portal.E2E.Test
1 MeshWeaver.Reactive.Assertions.Test
1 MeshWeaver.Search.Test
1 MeshWeaver.Speech.Test
0 MeshWeaver.Data.TestDomain
0 MeshWeaver.Hub.Fixture
0 MeshWeaver.Kernel.Test
0 MeshWeaver.Maui.Abstractions.Test
0 MeshWeaver.Maui.E2E.Test
0 MeshWeaver.PluginImage.Test
0 MeshWeaver.TestDomain
EOF
)

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
