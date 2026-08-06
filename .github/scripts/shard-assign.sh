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
# Weights are wall-clock SECONDS, the MAX over two green runs (30903428924 and
# 30903158378, both 2026-08-04) so a shard is sized for the bad case, not the
# average. The per-project `[CI] <name> exit=` markers in the test job's log ARE
# the measurement: take the delta between each `::group::<name>` and its marker.
# Re-measure when a project's runtime changes materially — the previous table
# (2026-07-13) had drifted far enough to cost ~100 s of long pole (Monolith
# 200→345, Data 25→73, PluginTester 60→8, Persistence 165→97).
#
# The floor for ANY sharding is the heaviest single project: today
# Hosting.Monolith.Test at 345 s. Adding shards below that buys nothing.
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
345 MeshWeaver.Hosting.Monolith.Test 2
263 MeshWeaver.AI.Test 2
239 MeshWeaver.Hosting.Orleans.Test
130 MeshWeaver.Hosting.PostgreSql.Test
104 MeshWeaver.Threading.Test
97 MeshWeaver.Persistence.Test
91 MeshWeaver.Content.Test
73 MeshWeaver.FutuRe.Test
73 MeshWeaver.Data.Test
62 MeshWeaver.Acme.Test
53 MeshWeaver.Security.Test
51 MeshWeaver.GitSync.Test
49 MeshWeaver.Autocomplete.Test
49 MeshWeaver.Query.Test
33 MeshWeaver.Auth.Test
31 MeshWeaver.Graph.Test
30 MeshWeaver.Hosting.Cosmos.Test
30 MeshWeaver.PluginCatalog.Test
27 MeshWeaver.NodeOperations.Test
26 MeshWeaver.InstanceSync.Test
22 MeshWeaver.Messaging.Hub.Test
18 MeshWeaver.Layout.Test
15 MeshWeaver.Markdown.Test
15 MeshWeaver.Import.Test
14 MeshWeaver.Courses.Test
12 MeshWeaver.PathResolution.Test
12 MeshWeaver.ContentCollections.Indexing.Graph.Test
12 MeshWeaver.MemexTemplate.Test
9 MeshWeaver.Northwind.Test
8 MeshWeaver.PluginTester.Test
8 MeshWeaver.PythonDemo.Test
7 MeshWeaver.ContentCollections.Indexing.PostgreSql.Test
6 MeshWeaver.AccessControl.Test
5 MeshWeaver.Hosting.Grpc.Test
5 MeshWeaver.Todo.Test
4 MeshWeaver.MathDemo.Test
4 MeshWeaver.Hosting.Test
3 Memex.Portal.Shared.Test
3 MeshWeaver.Social.Test
3 MeshWeaver.Hosting.Sqlite.Test
3 MeshWeaver.Hosting.Blazor.Test
3 MeshWeaver.ContentCollections.Test
3 MeshWeaver.Documentation.Test
2 MeshWeaver.Serialization.Test
2 MeshWeaver.Markdown.Export.Test
1 MeshWeaver.TestDomain
1 MeshWeaver.Data.TestDomain
1 MeshWeaver.Hub.Fixture
1 MeshWeaver.AI.Test.FakeCli
1 MeshWeaver.Portal.E2E.Test
1 MeshWeaver.Search.Test
1 MeshWeaver.Reactive.Assertions.Test
1 MeshWeaver.DataSetReader.Test
1 MeshWeaver.Markdown.Collaboration.Test
1 MeshWeaver.Maui.E2E.Test
1 MeshWeaver.Maui.Abstractions.Test
1 MeshWeaver.Maui.Integration.Test
1 MeshWeaver.PluginImage.Test
1 MeshWeaver.Hosting.Snowflake.Test
1 MeshWeaver.Connection.SignalR.Test
1 MeshWeaver.Speech.Test
1 MeshWeaver.ContentCollections.Indexing.Test
1 MeshWeaver.Kernel.Test
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
