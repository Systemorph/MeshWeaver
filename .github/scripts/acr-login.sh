#!/usr/bin/env bash
# Bounded transport retry for `az acr login` — the SAME idiom the bake job's
# "ACR login + pull" step proved out (5 attempts, 5s→40s exponential backoff,
# loud between attempts, red after the last).
#
# Why this exists: ACR reachability from a single runner is INTERMITTENT per
# job, not an outage — measured three distinct symptoms in one evening
# (2026-08-30): `Connection refused` (16:35, migration), 416
# `RequestedRangeNotSatisfiable` (18:53, portal-ai), and
# `az acr login … context deadline exceeded` (23:43, portal-ai) — each while
# SIBLING jobs in the same run logged into the same registry minutes earlier
# and pushed successfully (#2827). The portal-ai PUBLISH already carries a
# bounded, class-discriminating retry that even re-logins between attempts —
# but the INITIAL login step sat outside it, so the one transport failure the
# retry could not see was the one that blocked delivery.
#
# Login is pure transport: there is no compiler-diagnostics class to
# discriminate here (that discrimination belongs to the publish retry, #2839),
# so a plain bounded retry suppresses nothing deterministic.
set -uo pipefail
attempts=5
for attempt in $(seq 1 "$attempts"); do
  if az acr login --name meshweaver; then
    [ "$attempt" -gt 1 ] && echo "acr-login: reached the registry on attempt $attempt"
    exit 0
  fi
  if [ "$attempt" -lt "$attempts" ]; then
    delay=$((5 * 2 ** (attempt - 1)))
    echo "acr-login: attempt $attempt failed; retrying in ${delay}s" >&2
    sleep "$delay"
  fi
done
echo "::error::az acr login failed after $attempts attempts — the registry was unreachable from this runner for the whole backoff window (~75s), not a blip. See #2827 for the symptom family."
exit 1
