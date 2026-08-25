#!/bin/bash
# Bake content with the COMPILER, then gate the BAKED BYTES with the mesh (#2064).
#
# 🚨 This is the split main-cd's publish-bake already makes (`compile … --output`, then `… --seed`),
# lifted into a script so the PR lane cannot drift from it. Before #2064 the PR lane ran the FUSED
# shape — one `mw-plugin-test <stage> --bake-output <dir>` that stood up an in-process mesh, let the
# MESH compile every NodeType and collected what it produced. Producing an assembly is a build step;
# the mesh's job is to CONSUME a bake, not to be the thing that makes one.
#
# The split is also strictly stronger as a gate. Fused, the mesh rendered and ran `Tests` on a
# private recompile — bytes nothing ever ships. Seeded, it renders and tests the assemblies the bake
# actually produced, which are the assemblies a portal adopts.
#
#   usage: bake-then-gate.sh <tester-dll> <stage-dir> <bake-dir> <allow-file> <source-sha> <log-prefix>
#
# Writes <log-prefix>-bake.log (the compile) and <log-prefix>.log (the gate); the caller greps the
# latter for the tester's own `GATE FAILED` verdict line, exactly as it did when the run was fused.
# Exits with the gate's exit code, or 1 when a postcondition fails.
set -euo pipefail

TESTER="${1:?usage: bake-then-gate.sh <tester-dll> <stage-dir> <bake-dir> <allow-file> <source-sha> <log-prefix>}"
STAGE="${2:?stage-dir}"
BAKE="${3:?bake-dir}"
ALLOW="${4:?allow-file}"
SOURCE_SHA="${5:?source-sha}"
LOG_PREFIX="${6:?log-prefix}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bake_log="${LOG_PREFIX}-bake.log"
gate_log="${LOG_PREFIX}.log"
mkdir -p "$BAKE"

# ── 1. THE BAKE — compiler-driven, no mesh anywhere in it ───────────────────────────────────────
echo "── baking $STAGE → $BAKE (compiler-driven, no mesh)"
bake_status=0
if ! time dotnet "$TESTER" compile "$STAGE" \
    --output "$BAKE" --allow "$ALLOW" --source-sha "$SOURCE_SHA" 2>&1 | tee "$bake_log"; then
  bake_status="${PIPESTATUS[0]}"
  # tee always exits 0, so a non-zero pipeline with a zero head means tee itself failed.
  if [ "$bake_status" = "0" ]; then bake_status=1; fi
fi
if [ "$bake_status" != "0" ]; then
  echo "::error::the compiler-driven bake of $STAGE failed (exit $bake_status) — see the log above."
  exit "$bake_status"
fi

# ── 2. THE GATE — a mesh, CONSUMING the bake above ──────────────────────────────────────────────
echo "── gating $STAGE against the bake in $BAKE"
gate_status=0
if ! time dotnet "$TESTER" "$STAGE" --allow "$ALLOW" --seed "$BAKE" 2>&1 | tee "$gate_log"; then
  gate_status="${PIPESTATUS[0]}"
  if [ "$gate_status" = "0" ]; then gate_status=1; fi
fi

# ── 3. THE POSTCONDITIONS — only on a GREEN gate ────────────────────────────────────────────────
# A red gate already carries its own cause; re-judging it here could only REPLACE a real verdict
# with a derived one (the #1077 mistake, where a `tests` failure was annotated as "a public API
# changed" and sent investigators to diff signatures that were fine). These assertions exist to turn
# a GREEN run that adopted nothing into a red one — they add a RED, they never rewrite one.
if [ "$gate_status" != "0" ]; then
  exit "$gate_status"
fi

bash "$HERE/assert-bake-consumption.sh" "$BAKE" "$bake_log" "$gate_log" "$STAGE"
