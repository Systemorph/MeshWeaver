#!/usr/bin/env bash
#
# post-self-update-announcement.sh — sign and POST ONE `self-update-available` body (composed by
# `arm-promoted-set.py control-announcement`) into the control plane's Hosting/PlatformBuilds inbox,
# and read the answer. The ONE delivery half of "the line's first instance runs it first" (policies
# `platform-deploy-control-first`, `line-first-instance`): main-cd's `control-first` hands control its
# build with it, `first-instance-first` hands a line's declared first instance its build. One copy,
# so the two handovers can never disagree about what a 200 proves.
#
# Environment (never argv — the secret must not land in `ps` output):
#   BODY     the compact JSON the caller composed; the bytes signed are the bytes sent
#   URL      vars.CONTROL_WEBHOOK_URL — https://<control plane>/api/hooks/Hosting/PlatformBuilds
#   SECRET   secrets.CONTROL_WEBHOOK_SECRET — the HMAC the inbox verifies X-Hub-Signature-256 against
#   WHO      the deployment the build is handed to (`control`, `memex`, …) — for the messages only
#   VERSION  the build handed over — for the messages only
#
# Every non-success exits 1: a build the instance was never handed is the outage this delivers.
set -euo pipefail

for v in BODY URL SECRET WHO VERSION; do
  [ -n "${!v:-}" ] || { echo "::error::post-self-update-announcement: \$$v is empty — refusing to sign or send anything"; exit 1; }
done
case "$URL" in
  https://*/api/hooks/Hosting/PlatformBuilds) ;;
  *) echo "::error::vars.CONTROL_WEBHOOK_URL is not an https URL of a Hosting/PlatformBuilds inbox — refusing to sign anything to it"; exit 1 ;;
esac
resp="${RUNNER_TEMP:-/tmp}/announce-resp-$$"
: > "$resp"
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | sed 's/^.*= /sha256=/')
code=$(curl -sS -o "$resp" -w '%{http_code}' --connect-timeout 10 --max-time 60 \
  -X POST "$URL" -H 'Content-Type: application/json' \
  -H "X-Hub-Signature-256: $SIG" --data "$BODY") || code=000
echo "POST $URL -> $code"; cat "$resp"
verdict=$(tr -d '\n' < "$resp" | sed -n 's/.*"signature"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
case "$code" in
  2*)
    # Three answers, as `Did the inbox VERIFY the build fact?` reads them (#3338): `verified` passes;
    # any other WORD is the receiver saying the HMAC we sent was not checked — RED; NO verdict is a
    # receiver older than #3312 that cannot answer (its watcher still verifies before routing) — said,
    # never read as a pass or a refusal.
    case "$verdict" in
      verified) ;;
      "") echo "::warning::the control plane's inbox stored the announcement but answered no signature verdict (a pre-#3312 endpoint) — its watcher verifies before routing; read Ops/Actions/selfupdate-roll-$WHO-* to confirm the Roll opened" ;;
      *) echo "::error::the control plane's inbox stored the announcement but did not VERIFY its signature (signature: $verdict) — the router drops what it cannot verify, so $WHO would not be rolled. Declare WebhookInbox__Targets__N__SecretConfigKey=Hosting:PlatformWebhookSecret on that instance."
         exit 1 ;;
    esac
    # 🚨 STORED AND VERIFIED IS NOT DELIVERED (policy `control-first-never-silent`). The inbox answers
    # before its router runs, and the router DROPS an announcement whose `deployment` names no record
    # the receiving plane holds (SelfUpdateRouting: "the record lookup IS the authorization") —
    # measured 2026-10-07 from ci.10184 on: `200 accepted, verified` at a plane holding no Deployments
    # record, every build dropped, control frozen 10+ h. So this line claims only what the 200 proves;
    # whether the instance RUNS the build is read by `arm` (red past the bound).
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
      echo "- $WHO handed \`$VERSION\`: \`self-update-available\` for \`$WHO\` STORED and signature-verified at \`${URL%/api/hooks/*}\` (HTTP $code). That is not yet a roll: the plane opens one only if it holds the \`$WHO\` record — \`arm\` reads whether $WHO actually runs this build" >> "$GITHUB_STEP_SUMMARY"
    fi
    ;;
  000) echo "::error::could not reach the control plane's inbox at all (DNS/TLS/connection refused) — $WHO was NOT handed $VERSION"; exit 1 ;;
  401) echo "::error::the control plane's inbox REFUSED the signature (HTTP 401): secrets.CONTROL_WEBHOOK_SECRET and that instance's Hosting:PlatformWebhookSecret are not byte-identical — $WHO was NOT handed $VERSION"; exit 1 ;;
  404) echo "::error::the control plane's inbox answered 404 (an unlisted target or a wrong URL) — check vars.CONTROL_WEBHOOK_URL; $WHO was NOT handed $VERSION"; exit 1 ;;
  *)   echo "::error::the control plane's inbox refused the announcement (HTTP $code) — $WHO was NOT handed $VERSION"; exit 1 ;;
esac
