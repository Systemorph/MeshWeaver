#!/bin/bash
# The postconditions of a bake→gate pair (#2064): did the bake PRODUCE bytes, and did the gate
# actually JUDGE those bytes?
#
# 🚨 Why this needs to exist at all. Adoption is INVISIBLE in a gate verdict by construction: a
# NodeType the gate compiled itself renders and runs its `Tests` area exactly like one it adopted
# from the bake. So the entire consuming half can stop working — every portal falling back to
# Roslyn at boot — with every CI run still green. That is the same shape as a skipped job wearing a
# passing tick, and it is what #2064 is about.
#
#   usage: assert-bake-consumption.sh <bake-dir> <bake-log> <gate-log> [<label>]
#   exit 0 = the bake produced bundles AND the gate consumed them
#   exit 1 = it did not, naming which postcondition failed
set -euo pipefail

BAKE="${1:?usage: assert-bake-consumption.sh <bake-dir> <bake-log> <gate-log> [<label>]}"
BAKE_LOG="${2:?bake-log}"
GATE_LOG="${3:?gate-log}"
LABEL="${4:-$BAKE}"

fail() { echo "::error::$1"; exit 1; }

# ── 1. The bake PRODUCED something ──────────────────────────────────────────────────────────────
# Postconditions on a step that already reported green — never an input-shaped `if:`. AGENTS.md's
# distinction: a gate must not ask whether its input exists, but it must assert that a stage which
# claimed success left behind what success means.
[ -s "$BAKE/framework-mvid.txt" ] \
  || fail "$LABEL: the bake ran green but wrote no identity ($BAKE/framework-mvid.txt) — the bake stage regressed."
ls "$BAKE"/*.zip >/dev/null 2>&1 \
  || fail "$LABEL: the bake ran green but produced no bundles under $BAKE — the gate would then adopt nothing and pass vacuously."

# ── 2. No compile FALLBACK, and no FALSE REFUSAL ────────────────────────────────────────────────
# Each phrase means a consumer wanted prebuilt bytes, could not get them, and quietly compiled
# instead. Green in every other signal; a delivery defect all the same.
#
# 🚨 "ADOPTION REFUSED" is the #2813 phrase, and it is here for the OPPOSITE failure to the others.
# The refusal fires when a bundle's recorded source fingerprint disagrees with the source the mesh
# holds — which, in THIS lane, cannot legitimately happen: the bake and the gate are staged from the
# same tree by bake-then-gate.sh, so a disagreement means the two producers HASH THE SAME CONTENT
# DIFFERENTLY. That is a false refusal, and shipping one refuses every good bundle in the fleet —
# strictly worse than the staleness bug the refusal exists to catch. It is also invisible without
# this line: `Seed` returns TRUE and the adopted/declared counts below still balance, because the
# owner refuses AFTERWARDS, when it stamps; the type then flips to Pending, recompiles locally, and
# every per-type verdict is green. The gate's own default log level is Warning and the refusal logs
# at Error/Critical, so the phrase reaches the log without turning anything up.
fallback_hit=0
while IFS= read -r phrase; do
  [ -z "$phrase" ] && continue
  if grep -F -q -- "$phrase" "$BAKE_LOG" "$GATE_LOG" 2>/dev/null; then
    # The refusal phrase means something different from the rest, so it must not be reported as a
    # fallback — a reader sent to look for a declined bundle would find a perfectly adopted one.
    if [ "$phrase" = "ADOPTION REFUSED" ]; then
      echo "::error::$LABEL: FALSE REFUSAL (#2813) — a NodeType refused the bake's own bytes as built from different source, but bake and gate were staged from the SAME tree. The compiler-driven producer and the runtime therefore hash the same content differently, which refuses every good bundle in the fleet. Fix NodeTypeSourceFingerprint (or whichever side moved) before shipping; do NOT relax this check."
    else
      echo "::error::$LABEL: compile fallback detected — \"$phrase\". A consumer declined the bake and compiled instead, so this run did not judge the bytes the bake shipped."
    fi
    grep -F -n -- "$phrase" "$BAKE_LOG" "$GATE_LOG" 2>/dev/null | head -5
    fallback_hit=1
  fi
done <<'PHRASES'
no prebuilt bundle
carried no assemblies
could resolve NO artifact
no prebuilt assemblies adopted
ADOPTION REFUSED
PHRASES

# ── 3. The gate CONSUMED the bake ───────────────────────────────────────────────────────────────
seed_line="$(grep -m1 -- '^seed: adopted ' "$GATE_LOG" || true)"
[ -n "$seed_line" ] \
  || fail "$LABEL: the gate ran green but printed no 'seed:' line — it was given no bake to consume, so it judged a private recompile instead of the bytes the bake shipped."

adopted="$(printf '%s' "$seed_line" | sed -n 's/^seed: adopted \([0-9][0-9]*\) of \([0-9][0-9]*\) .*/\1/p')"
expected="$(printf '%s' "$seed_line" | sed -n 's/^seed: adopted \([0-9][0-9]*\) of \([0-9][0-9]*\) .*/\2/p')"
{ [ -n "$adopted" ] && [ -n "$expected" ]; } \
  || fail "$LABEL: could not read the adopted/declared counts from the gate's seed line: '$seed_line'"

echo "$LABEL: seed postcondition — adopted=$adopted declared=$expected"

# 🚨 THE LOAD-BEARING CHECK. BakeSeed.Shortfall() returns null — a PASS — when the bake declared
# NOTHING, because "adopted everything declared" is vacuously true over an empty bake. So a
# mis-staged or empty bake yields a gate that consumed zero bytes and still reports success. The
# tester cannot close this (it has no way to know the bake should have been non-empty); the caller
# can, because it just ran the bake.
[ "$expected" -gt 0 ] \
  || fail "$LABEL: the gate adopted $adopted of 0 baked assembly(ies) — the bake declared nothing this run installed, so the gate judged NONE of the bytes that ship. Bake and gate must be staged from the same tree."

# 🚨 `=`, and it can be `=` since #2697. This was `>=` because the tester reported adoption EVENTS:
# the gate installs every package TWICE (the idempotence pin re-installs the unchanged snapshot),
# so the second install adopted the same assemblies again and a healthy run measured `adopted 32 of
# 28` on samples/Graph/Data. An equality test would have been red on every healthy run then — and a
# gate that cries wolf gets switched off.
#
# The tester now reports BakeSeedConsumer.AdoptedPaths: the size of the set of distinct paths that
# were declared AND requested AND backed — the exact complement of the DECLINED list in the same
# verdict. It cannot exceed `expected` however many times the seeder acted, so `>=` had become a
# strictly weaker way of writing `=`, and the reason for the weakness was a bug rather than a
# property of the run. Equality now says what this check actually means: every baked assembly this
# run installed was judged from the bake's own bytes.
[ "$adopted" -eq "$expected" ] \
  || fail "$LABEL: the gate adopted only $adopted of $expected baked assembly(ies) — the rest were DECLINED and compiled locally. PrebuiltAssemblySeeder logs the per-assembly reason (framework identity, or the per-type dependency record)."

[ "$fallback_hit" = "0" ] || exit 1

echo "$LABEL: bake produced $(ls "$BAKE"/*.zip | wc -l | tr -d ' ') bundle(s); gate adopted $adopted/$expected baked assembly(ies)."
exit 0
