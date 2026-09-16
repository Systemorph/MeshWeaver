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
#   usage: bake-then-gate.sh <tester-dll> <stage-dir> <bake-dir> <allow-file> <source-sha> \
#                            <log-prefix> <warning-baseline>
#
# Writes <log-prefix>-bake.log (the compile) and <log-prefix>.log (the gate); the caller greps the
# latter for the tester's own `GATE FAILED` verdict line, exactly as it did when the run was fused.
# Exits with the gate's exit code, or 1 when a postcondition fails.
#
# <warning-baseline> arms the two shrink-only in-mesh warning ratchets (one over the real warnings,
# a separate one over CS1591) — see Doc/Architecture/InMeshWarningStandard. It is REQUIRED here and
# its enforcement is asserted below: the tester's default is observe-only, so a dropped argument
# would leave in-mesh C# with no warning standard at all while every run stayed green, which is the
# exact shape "a gate that cannot fail is not a gate" forbids.
set -euo pipefail

TESTER="${1:?usage: bake-then-gate.sh <tester-dll> <stage-dir> <bake-dir> <allow-file> <source-sha> <log-prefix> <warning-baseline>}"
STAGE="${2:?stage-dir}"
BAKE="${3:?bake-dir}"
ALLOW="${4:?allow-file}"
SOURCE_SHA="${5:?source-sha}"
LOG_PREFIX="${6:?log-prefix}"
WARNING_BASELINE="${7:?warning-baseline}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bake_log="${LOG_PREFIX}-bake.log"
gate_log="${LOG_PREFIX}.log"
mkdir -p "$BAKE"

# ── 1. THE BAKE — compiler-driven, no mesh anywhere in it ───────────────────────────────────────
echo "── baking $STAGE → $BAKE (compiler-driven, no mesh)"
bake_status=0
if ! time dotnet "$TESTER" compile "$STAGE" \
    --output "$BAKE" --allow "$ALLOW" --source-sha "$SOURCE_SHA" \
    --warning-baseline "$WARNING_BASELINE" 2>&1 | tee "$bake_log"; then
  bake_status="${PIPESTATUS[0]}"
  # tee always exits 0, so a non-zero pipeline with a zero head means tee itself failed.
  if [ "$bake_status" = "0" ]; then bake_status=1; fi
fi
if [ "$bake_status" != "0" ]; then
  echo "::error::the compiler-driven bake of $STAGE failed (exit $bake_status) — see the log above."
  exit "$bake_status"
fi

# ── 1b. THE RATCHETS RAN, AND BOTH OF THEM ─────────────────────────────────────────────────────
# 🚨 A POSTCONDITION on a GREEN bake, not a hope. `--warning-baseline` is what arms the two in-mesh
# warning ratchets; without it the tester MEASURES and enforces nothing, exits 0, and prints
# "OBSERVE-ONLY" — which is the honest behaviour for a repo that has no baseline yet and a silent
# hole for this one. Drop the argument in a future edit and every run below would stay green having
# judged no warning at all. Asserting the two ENFORCED lines makes that edit RED instead. Both are
# named individually: one ratchet armed and the other not is the failure a single grep would miss.
for ratchet in warnings doc-comments; do
  if ! grep -q "^warnings: ${ratchet} ratchet — ENFORCED" "$bake_log"; then
    echo "::error::the bake of $STAGE did not ENFORCE its '${ratchet}' ratchet (expected a 'warnings: ${ratchet} ratchet — ENFORCED' line in $bake_log). --warning-baseline '$WARNING_BASELINE' was passed but the tester reports it did not arm — in-mesh C# would be held to no standard while this step stayed green."
    exit 1
  fi
done

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
