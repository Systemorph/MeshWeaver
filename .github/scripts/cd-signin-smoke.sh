#!/usr/bin/env bash
#
# Boot the image PAIR a CD run built — migration, then portal — against a fresh Postgres, sign in
# through DevLogin, and prove that a SIGNED-IN request is served.
#
#   .github/scripts/cd-signin-smoke.sh --portal-image REF --migration-image REF [options]
#   exit 0 = the pair serves a signed-in request   exit 1 = it does not, or the proof could not run
#
# 🚨 WHY THIS EXISTS (2026-09-03, MeshWeaver#3206 × Plugins#1263). CD run #7658 built
# memex-portal-ai:3.0.0-rc9.ci.7658 from core e7f1d699 (08:05Z, BEFORE core #3206 anchored the
# sign-in reads) paired with the Plugins head at resolve time, 12500c9 (13:31Z, AFTER Plugins #1263
# made the Postgres planner REFUSE an unanchored query). Every leg was green. `promote` tagged the
# pair, "Verify every image shipped" found every tag, and the first signed-in request in production
# faulted in OnboardingMiddleware.LoadUserRoles with UnanchoredQueryException: the portal answered
# 503 to EVERY signed-in request for 20 minutes, and the Store page died the same way. Nothing in CD
# had ever BOOTED the pair; nothing had ever SIGNED IN. The two halves can be hours apart because a
# run's jobs queue for hours, and no job checked that the pair was coherent BEHAVIOURALLY.
#
# 🚨 THE ASSERTIONS THAT MATTER CARRY THE COOKIE. ci.7658 answered 200 to an anonymous GET / and to
# /_blazor/negotiate — only requests carrying a signed-in cookie 503'd. So an unauthenticated 200
# proves nothing here (the same trap as /api/og answering 200 during a content outage), and this
# script fails only on what the incident actually broke: the SIGNED-IN GET / and GET /Store, plus
# the exact log signatures. An anonymous probe is logged for diagnosis and never asserted.
#
# 🚨 FAIL CLOSED. A pull that never completes, a migration that never logs its version, a /health
# that never answers, a DevLogin that answers 400 — every one is exit 1. "Could not check" must
# never be reported as "checked and fine". There is deliberately no --dry-run: a run of this script
# that touched no image is not a run of this script.
#
# What it cannot prove: anything behind a language model, a registry bundle, Entra sign-in, or
# multi-replica AdoNet clustering — none of those are in the box. It proves the pair BOOTS on a
# fresh schema, that the schema the migration wrote is the one the portal accepts (DbVersionGate),
# and that the sign-in → identity → roles → render path serves a real user. That is the path the
# incident broke, and it is the path every user takes first.
#
# Runnable locally against the real registry, which is how it was verified (positive AND negative
# control — the pair that shipped fixed passes, the pair that 503'd fails):
#   az acr login -n meshweaver
#   .github/scripts/cd-signin-smoke.sh --platform linux/arm64 \
#     --portal-image meshweaver.azurecr.io/memex-portal-ai:3.0.0-rc9.ci.7693 \
#     --migration-image meshweaver.azurecr.io/memex-migration:3.0.0-rc9.ci.7693
set -uo pipefail

PORTAL_IMAGE=""
MIGRATION_IMAGE=""
PLATFORM="linux/amd64"
# pgvector: SchemaInitialization creates `vector` columns, so a bare postgres image fails the
# migration for a reason that has nothing to do with the pair. Same family the chart and compose
# ship (pgvector/pgvector:pg17); pg16 is the floor the gate was written against.
POSTGRES_IMAGE="pgvector/pgvector:pg16"
CORE_SHA="(not given)"
PLUGINS_SHA="(not given)"
STAGING_TAG="(not given)"
HEALTH_DEADLINE=480
MIGRATION_DEADLINE=300
PULL=1
KEEP=0
SUMMARY="${GITHUB_STEP_SUMMARY:-}"

usage() {
  sed -n '2,/^set -uo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
  cat <<EOF
Options:
  --portal-image REF       the memex-portal-ai image under test (required)
  --migration-image REF    the memex-migration image under test (required)
  --platform P             docker platform to pull and run (default: $PLATFORM)
  --postgres-image REF     the database container (default: $POSTGRES_IMAGE)
  --core-sha SHA           recorded in the summary — the core commit the pair was built from
  --plugins-sha SHA        recorded in the summary — the MeshWeaver.Plugins commit
  --staging-tag TAG        recorded in the summary — the run's staging tag
  --health-deadline SEC    seconds to wait for /health to answer 200 (default: $HEALTH_DEADLINE)
  --migration-deadline SEC seconds to wait for the migration to exit (default: $MIGRATION_DEADLINE)
  --summary FILE           markdown summary target (default: \$GITHUB_STEP_SUMMARY, else none)
  --no-pull                use the images already present locally
  --keep                   leave the containers running for inspection
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --portal-image)        PORTAL_IMAGE="$2"; shift 2 ;;
    --migration-image)     MIGRATION_IMAGE="$2"; shift 2 ;;
    --platform)            PLATFORM="$2"; shift 2 ;;
    --postgres-image)      POSTGRES_IMAGE="$2"; shift 2 ;;
    --core-sha)            CORE_SHA="$2"; shift 2 ;;
    --plugins-sha)         PLUGINS_SHA="$2"; shift 2 ;;
    --staging-tag)         STAGING_TAG="$2"; shift 2 ;;
    --health-deadline)     HEALTH_DEADLINE="$2"; shift 2 ;;
    --migration-deadline)  MIGRATION_DEADLINE="$2"; shift 2 ;;
    --summary)             SUMMARY="$2"; shift 2 ;;
    --no-pull)             PULL=0; shift ;;
    --keep)                KEEP=1; shift ;;
    -h|--help)             usage; exit 0 ;;
    *) echo "::error::unknown argument '$1'"; usage >&2; exit 2 ;;
  esac
done
[ -n "$PORTAL_IMAGE" ]    || { echo "::error::--portal-image is required"; exit 2; }
[ -n "$MIGRATION_IMAGE" ] || { echo "::error::--migration-image is required"; exit 2; }
command -v docker >/dev/null || { echo "::error::docker is not on PATH — this gate cannot run"; exit 2; }
command -v curl   >/dev/null || { echo "::error::curl is not on PATH — this gate cannot run"; exit 2; }
docker info >/dev/null 2>&1 || { echo "::error::the docker daemon is not reachable — this gate cannot run"; exit 2; }

# ── naming, workspace, summary, cleanup ─────────────────────────────────────────────────────
RUN="cdsmoke-$(date -u +%s)-$$"
NET="$RUN-net"; PG="$RUN-pg"; MIG="$RUN-mig"; PORTAL="$RUN-portal"; VOL="$RUN-data"
WORK="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/$RUN.XXXX")"
JAR="$WORK/cookies.txt"
PG_PASSWORD="smoke-$RANDOM$RANDOM"
CONN="Host=$PG;Port=5432;Username=postgres;Password=$PG_PASSWORD;Database=memex"
FAILED=""
MIGRATION_VERSION="(never reached)"
PORTAL_READY_AFTER="(never)"

summary() { [ -n "$SUMMARY" ] && echo "$1" >> "$SUMMARY"; return 0; }
ok()      { echo "✅ $1";          summary "- ✅ $1"; }
info()    { echo "ℹ️  $1";         summary "- $1"; }
fail()    { echo "::error::$1";    summary "- ❌ $1"; FAILED="${FAILED:+$FAILED; }$1"; }

group()    { [ -n "${GITHUB_ACTIONS:-}" ] && echo "::group::$1" || echo "── $1"; return 0; }
endgroup() { [ -n "${GITHUB_ACTIONS:-}" ] && echo "::endgroup::"; return 0; }

# On failure the two container logs go into the job log — warnings/errors FIRST, so the line that
# names the fault is at the top rather than 800 lines down a boot transcript.
dump_logs() {
  local name="$1" file="$2"
  [ -s "$file" ] || { echo "(no log captured for $name)"; return 0; }
  # Console-logger level prefixes plus the incident vocabulary — not a bare `error`, which the
  # migration's echoed SQL (`EXCEPTION WHEN …`, `-- Fail-safe: …`) matches on every line.
  group "$name — warning/error lines"
  grep -nE '^(warn|fail|crit):|Exception|Refusing to start|UNAVAILABLE for|Error:' "$file" | head -200 || echo "(none)"
  endgroup
  group "$name — last 300 lines"
  tail -n 300 "$file"
  endgroup
}

cleanup() {
  local rc=$?
  if [ -n "$FAILED" ]; then
    [ -f "$WORK/migration.log" ] || docker logs "$MIG" > "$WORK/migration.log" 2>&1 || true
    docker logs "$PORTAL" > "$WORK/portal.log" 2>&1 || true
    dump_logs "migration ($MIGRATION_IMAGE)" "$WORK/migration.log"
    dump_logs "portal ($PORTAL_IMAGE)" "$WORK/portal.log"
  fi
  if [ "$KEEP" -eq 1 ]; then
    echo "--keep: leaving $PORTAL / $MIG / $PG on network $NET (volume $VOL); work dir $WORK"
  else
    docker rm -f "$PORTAL" "$MIG" "$PG" >/dev/null 2>&1 || true
    docker network rm "$NET" >/dev/null 2>&1 || true
    docker volume rm "$VOL" >/dev/null 2>&1 || true
    rm -rf "$WORK"
  fi
  exit "$rc"
}
trap cleanup EXIT

echo "Pair under test: core $CORE_SHA + plugins $PLUGINS_SHA (staging tag $STAGING_TAG)"
echo "  portal    = $PORTAL_IMAGE"
echo "  migration = $MIGRATION_IMAGE"
echo "  platform  = $PLATFORM · database = $POSTGRES_IMAGE"
summary "### Sign-in smoke: boot the image pair and sign in"
summary "| | |"
summary "|---|---|"
summary "| core sha | \`$CORE_SHA\` |"
summary "| plugins sha | \`$PLUGINS_SHA\` |"
summary "| staging tag | \`$STAGING_TAG\` |"
summary "| portal image | \`$PORTAL_IMAGE\` |"
summary "| migration image | \`$MIGRATION_IMAGE\` |"
summary "| platform | \`$PLATFORM\` · database \`$POSTGRES_IMAGE\` |"
summary ""

# ── 1. pull (infra, bounded retry — the same idiom publish-bake uses) ───────────────────────
pull() {
  local image="$1" attempts=5 attempt delay
  for attempt in $(seq 1 $attempts); do
    if docker pull --quiet --platform "$PLATFORM" "$image" >/dev/null; then return 0; fi
    if [ "$attempt" -lt "$attempts" ]; then
      delay=$((5 * 2 ** (attempt - 1)))
      echo "pull of $image failed (attempt $attempt); retrying in ${delay}s" >&2
      sleep "$delay"
    fi
  done
  return 1
}
if [ "$PULL" -eq 1 ]; then
  for image in "$MIGRATION_IMAGE" "$PORTAL_IMAGE" "$POSTGRES_IMAGE"; do
    if ! pull "$image"; then
      fail "could not pull $image for $PLATFORM after 5 attempts — a REGISTRY/INFRA failure, not a verdict on the pair"
      exit 1
    fi
  done
  ok "pulled all three images for $PLATFORM"
else
  ok "using local images (--no-pull)"
fi

# ── 2. a fresh database ─────────────────────────────────────────────────────────────────────
docker network create "$NET" >/dev/null || { fail "could not create docker network $NET"; exit 1; }
docker run -d --name "$PG" --network "$NET" --platform "$PLATFORM" \
  -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB=memex "$POSTGRES_IMAGE" >/dev/null \
  || { fail "could not start $POSTGRES_IMAGE"; exit 1; }
# TCP, not the unix socket: the official image's initdb phase runs a temporary server that listens
# on the socket only, and pg_isready against it would report ready before the real server is up.
pg_deadline=$(( $(date +%s) + 90 ))
until docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres -d memex >/dev/null 2>&1; do
  if [ "$(date +%s)" -ge "$pg_deadline" ]; then
    docker logs "$PG" 2>&1 | tail -n 50
    fail "Postgres did not accept TCP connections within 90s"
    exit 1
  fi
  sleep 2
done
ok "Postgres ready (database memex, fresh)"

# ── 3. the migration, with the SAME env names the Helm chart and compose give it ───────────
docker run -d --name "$MIG" --network "$NET" --platform "$PLATFORM" \
  -e ConnectionStrings__memex="$CONN" \
  -e MEMEX_HOST="$PG" -e MEMEX_PORT=5432 -e MEMEX_USERNAME=postgres -e MEMEX_PASSWORD="$PG_PASSWORD" \
  -e MEMEX_DATABASENAME=memex \
  -e MEMEX_URI="postgresql://postgres:$PG_PASSWORD@$PG:5432/memex" \
  -e MEMEX_JDBCCONNECTIONSTRING="jdbc:postgresql://$PG:5432/memex" \
  "$MIGRATION_IMAGE" >/dev/null || { fail "could not start the migration container"; exit 1; }
mig_start=$(date +%s)
mig_deadline=$(( mig_start + MIGRATION_DEADLINE ))
while [ "$(docker inspect --format '{{.State.Status}}' "$MIG")" = "running" ]; do
  if [ "$(date +%s)" -ge "$mig_deadline" ]; then
    docker kill "$MIG" >/dev/null 2>&1 || true
    docker logs "$MIG" > "$WORK/migration.log" 2>&1 || true
    fail "the migration was still running after ${MIGRATION_DEADLINE}s — killed. A migration that does not complete cannot certify a schema"
    exit 1
  fi
  sleep 3
done
docker logs "$MIG" > "$WORK/migration.log" 2>&1 || true
mig_exit=$(docker inspect --format '{{.State.ExitCode}}' "$MIG")
if [ "$mig_exit" != "0" ]; then
  fail "the migration exited $mig_exit"
  exit 1
fi
# The literal line Program.cs logs LAST, after every phase — its absence with exit 0 is a
# migration that stopped short, and the version it names is what DbVersionGate will compare.
MIGRATION_VERSION=$(grep -oE 'Database migration completed\. Version: [0-9]+' "$WORK/migration.log" | tail -n 1 | grep -oE '[0-9]+$')
if [ -z "$MIGRATION_VERSION" ]; then
  MIGRATION_VERSION="(not logged)"
  fail "the migration exited 0 but never logged 'Database migration completed. Version: N'"
  exit 1
fi
ok "migration completed in $(( $(date +%s) - mig_start ))s — Database migration completed. Version: $MIGRATION_VERSION"

# ── 4. the portal, on a writable /data, DevLogin on, single local silo ──────────────────────
docker volume create "$VOL" >/dev/null || { fail "could not create volume $VOL"; exit 1; }
# The image runs as APP_UID 1654 and a fresh named volume is root-owned; the chart gives it a PVC
# and the e2e portal an emptyDir, both writable. Open it the same way rather than run as root.
docker run --rm --user root --entrypoint chmod -v "$VOL:/data" --platform "$PLATFORM" \
  "$PORTAL_IMAGE" 1777 /data >/dev/null || { fail "could not make /data writable"; exit 1; }
docker run -d --name "$PORTAL" --network "$NET" --platform "$PLATFORM" \
  -p 127.0.0.1::8080 -v "$VOL:/data" \
  -e ConnectionStrings__memex="$CONN" \
  -e MEMEX_HOST="$PG" -e MEMEX_PORT=5432 -e MEMEX_USERNAME=postgres -e MEMEX_PASSWORD="$PG_PASSWORD" \
  -e MEMEX_DATABASENAME=memex \
  -e MEMEX_URI="postgresql://postgres:$PG_PASSWORD@$PG:5432/memex" \
  -e MEMEX_JDBCCONNECTIONSTRING="jdbc:postgresql://$PG:5432/memex" \
  -e ASPNETCORE_HTTP_PORTS=8080 \
  -e Deployment__Backend=Filesystem \
  -e Deployment__DataRoot=/data \
  -e Deployment__Orleans__Clustering=Localhost \
  -e Storage__Name=content -e Storage__SourceType=FileSystem -e Storage__BasePath=/data/content \
  -e Graph__Storage__Type=PostgreSql -e Graph__Storage__BasePath=/data/graph \
  -e Authentication__EnableDevLogin=true \
  -e Portal__InstanceName="CD smoke" \
  "$PORTAL_IMAGE" >/dev/null || { fail "could not start the portal container"; exit 1; }
PORT=$(docker port "$PORTAL" 8080/tcp | head -n 1 | sed 's/.*://')
[ -n "$PORT" ] || { fail "could not resolve the portal's published port"; exit 1; }
BASE="http://127.0.0.1:$PORT"

portal_start=$(date +%s)
health_deadline=$(( portal_start + HEALTH_DEADLINE ))
while :; do
  state=$(docker inspect --format '{{.State.Status}}' "$PORTAL")
  if [ "$state" != "running" ]; then
    fail "the portal container is '$state' before /health ever answered 200 (exit $(docker inspect --format '{{.State.ExitCode}}' "$PORTAL"))"
    exit 1
  fi
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE/health" || true)
  if [ "$code" = "200" ]; then break; fi
  if [ "$(date +%s)" -ge "$health_deadline" ]; then
    fail "/health did not answer 200 within ${HEALTH_DEADLINE}s (last: '$code') — the portal never became ready on the schema the paired migration wrote"
    exit 1
  fi
  sleep 5
done
PORTAL_READY_AFTER="$(( $(date +%s) - portal_start ))s"
ok "portal /health answered 200 after $PORTAL_READY_AFTER"

# ── 5. sign in through DevLogin and keep the cookie ─────────────────────────────────────────
# POST /dev/signin (DevAuthController): form fields personId + returnUrl, answers 302 with the
# MemexAuth cookie; a brand-new personId is provisioned on first sign-in. A 400 here is
# "Person not found" — DevLogin is OFF (the host forces it off unless the env is literally true).
code=$(curl -s -o "$WORK/signin.body" -w '%{http_code}' --max-time 180 -c "$JAR" \
  -X POST --data-urlencode 'personId=cd-smoke' --data-urlencode 'returnUrl=/' "$BASE/dev/signin" || echo "curl-failed")
if [ "$code" != "302" ]; then
  fail "DevLogin POST /dev/signin answered $code, expected 302 — body: $(head -c 300 "$WORK/signin.body" | tr '\n' ' ')"
  exit 1
fi
if ! grep -q 'MemexAuth' "$JAR"; then
  fail "DevLogin answered 302 but set no MemexAuth cookie"
  exit 1
fi
ok "DevLogin as 'cd-smoke' → 302 + MemexAuth cookie"

# Anonymous probe — DIAGNOSTIC ONLY, never asserted: ci.7658 answered 200 here while 503'ing every
# signed-in request, so this number says nothing about the pair. It is printed so a future red can
# be read against it ("anonymous fine, signed-in 503" is exactly the incident's shape).
anon=$(curl -s -o /dev/null -w '%{http_code}' --max-time 60 "$BASE/" || echo "curl-failed")
info "anonymous GET / → $anon (diagnostic only — not an assertion)"

# ── 6. the SIGNED-IN requests — the assertions that matter ──────────────────────────────────
# Status 200 exactly: no redirect is followed, so a 302 to /login (cookie rejected) or /onboarding
# (identity not resolved) fails here rather than landing on a 200 page that proves nothing. The
# body must not carry the identity-unavailable text (issue #637's 503 page, in either shipped
# language) and must be the app shell, not an error page.
IDENTITY_UNAVAILABLE_EN="We could not check your account just now"
IDENTITY_UNAVAILABLE_DE="Ihr Konto konnte gerade nicht geprüft werden"
signed_in_get() { # signed_in_get <path> <label>
  local path="$1" label="$2" code body
  body="$WORK/$(echo "$path" | tr '/?' '__').body"
  code=$(curl -s -o "$body" -w '%{http_code}' --max-time 180 -b "$JAR" "$BASE$path" || echo "curl-failed")
  if [ "$code" != "200" ]; then
    fail "signed-in GET $path answered $code, expected 200 ($label) — body: $(head -c 300 "$body" | tr '\n' ' ')"
    return 1
  fi
  if grep -qF "$IDENTITY_UNAVAILABLE_EN" "$body" || grep -qF "$IDENTITY_UNAVAILABLE_DE" "$body"; then
    fail "signed-in GET $path answered 200 but rendered the identity-unavailable text ($label)"
    return 1
  fi
  if ! grep -qiE 'blazor|<app|<html' "$body"; then
    fail "signed-in GET $path answered 200 but the body is not the portal shell ($label) — first bytes: $(head -c 200 "$body" | tr '\n' ' ')"
    return 1
  fi
  ok "signed-in GET $path → 200, rendered, no identity-unavailable text ($label)"
}
signed_in_get "/"      "home — the request the 2026-09-03 incident 503'd"      || exit 1
signed_in_get "/Store" "Store — died the same way on ci.7658"                  || exit 1

code=$(curl -s -o "$WORK/negotiate.body" -w '%{http_code}' --max-time 60 -b "$JAR" \
  -X POST -H 'Content-Length: 0' "$BASE/_blazor/negotiate?negotiateVersion=1" || echo "curl-failed")
if [ "$code" != "200" ] || ! grep -q 'connectionId' "$WORK/negotiate.body"; then
  fail "signed-in POST /_blazor/negotiate answered $code (expected 200 with a connectionId) — body: $(head -c 300 "$WORK/negotiate.body" | tr '\n' ' ')"
  exit 1
fi
ok "signed-in POST /_blazor/negotiate → 200 with a connectionId"

# ── 7. the incident's exact log signatures — a status code is not the only evidence ────────
# `UNAVAILABLE for` is qualified with `answering 503` ON PURPOSE. Every site that turns an
# unresolved identity into a 503 logs "… UNAVAILABLE for {Path} ({Reason}) — answering 503 +
# Retry-After" (OnboardingMiddleware — the incident's line — plus the bearer/API-token/instance-key
# handlers). UserContextMiddleware ALSO logs "mesh user index UNAVAILABLE for {Email} … falling
# back to the email local-part", a documented cold-start fallback that fires on the first request
# after every fresh boot and drives nothing — measured on ci.7693: one such line per run, and the
# request it belongs to answered 200. A bare `UNAVAILABLE for` therefore fails the pair that
# shipped FIXED, and a gate that fails its own positive control is not a gate.
docker logs "$PORTAL" > "$WORK/portal.log" 2>&1 || true
SIGNATURES='UnanchoredQueryException|UNAVAILABLE for .*answering 503|Refusing to start'
hits=$(grep -cE "$SIGNATURES" "$WORK/portal.log" "$WORK/migration.log" | awk -F: '{s+=$NF} END {print s+0}')
if [ "$hits" != "0" ]; then
  group "signature hits"
  grep -nE "$SIGNATURES" "$WORK/portal.log" "$WORK/migration.log" | head -n 50
  endgroup
  fail "$hits log line(s) match the incident signatures ($SIGNATURES) — the pair served the requests but a read faulted underneath"
  exit 1
fi
ok "0 log lines match '$SIGNATURES' across portal + migration"

ready_line=$(grep -oE 'PortalReady[^"]{0,60}' "$WORK/portal.log" | head -n 1)
[ -n "$ready_line" ] && info "lifecycle: $ready_line"
summary ""
summary "**PASS** — the pair boots on a fresh schema (migration version $MIGRATION_VERSION), becomes healthy in $PORTAL_READY_AFTER, and serves a signed-in user."
echo "PASS: $PORTAL_IMAGE + $MIGRATION_IMAGE serve a signed-in request (migration version $MIGRATION_VERSION, healthy after $PORTAL_READY_AFTER)"
exit 0
