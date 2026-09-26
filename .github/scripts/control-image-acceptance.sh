#!/usr/bin/env bash
# control-image-acceptance.sh — the control image's acceptance test (Memex docs/control-instance.md
# §3.1, P1/P2). Boots the image the way the cluster judges it — the startupProbe path `/health`,
# then the readinessProbe path `/ready`, inside the chart's startup budget — against:
#
#   (a) an EMPTY Postgres, migrated by this run's migration image;
#   (b) the same database after a deliberately BROKEN NodeType row was written into a user-shaped
#       partition (created by the platform's own `public.ensure_partition_schema`, listed as
#       searchable) — the 2026-09-25 incident's shape: one abandoned in-mesh type;
#   (c) the PREVIOUS control image against the database THIS image's migration produced (N−1
#       against an N-migrated schema — expand-only migrations).
#
# Each leg must become Ready, or the script exits non-zero naming the leg, with the pod's /health
# body and the tail of its log. Nothing here is skipped silently: (c) runs unless --first-run is
# given, and --first-run says so on stdout as a GitHub `::notice::`.
#
# 🚨 READY IS NOT ENOUGH, AND THAT WAS MEASURED. A portal with no `Graph:Storage` serves the
# first-run SETUP wizard, composes no mesh, and answers 200 on /health and /ready within seconds —
# the first local run of this script passed all three legs that way and proved nothing. So each leg
# also requires EVIDENCE from the process itself: the mesh was composed (no setup wizard), the
# control module was loaded from the image's modules/, and the module's seed finished with no
# failure. Leg (b) further reads the database: the broken row was never compiled.
#
# usage:
#   control-image-acceptance.sh --control IMG --migration IMG (--previous IMG | --first-run)
#                               [--postgres IMG] [--budget SECONDS]
#
# Needs only docker. Every container and the network are removed on exit.
set -euo pipefail

CONTROL="" MIGRATION="" PREVIOUS="" FIRST_RUN=false
POSTGRES_IMAGE="pgvector/pgvector:pg17"
# The chart's startupProbe window: periodSeconds 5 × failureThreshold 60 (deploy/helm/values.yaml).
BUDGET=300

while [ $# -gt 0 ]; do
  case "$1" in
    --control) CONTROL="$2"; shift 2 ;;
    --migration) MIGRATION="$2"; shift 2 ;;
    --previous) PREVIOUS="$2"; shift 2 ;;
    --first-run) FIRST_RUN=true; shift ;;
    --postgres) POSTGRES_IMAGE="$2"; shift 2 ;;
    --budget) BUDGET="$2"; shift 2 ;;
    *) echo "::error::unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$CONTROL" ] || { echo "::error::--control is required"; exit 2; }
[ -n "$MIGRATION" ] || { echo "::error::--migration is required"; exit 2; }
if [ -z "$PREVIOUS" ] && [ "$FIRST_RUN" != true ]; then
  echo "::error::neither --previous nor --first-run: leg (c) must either run or be declared not applicable — never skipped by omission"
  exit 2
fi
if [ -n "$PREVIOUS" ] && [ "$FIRST_RUN" = true ]; then
  echo "::error::--previous and --first-run are mutually exclusive"
  exit 2
fi

RUN="ctlacc-$$"
NET="$RUN-net"
PG="$RUN-pg"
DB=control
CONN="Host=$PG;Port=5432;Database=$DB;Username=postgres;Password=postgres"

cleanup() {
  docker ps -aq --filter "name=^$RUN-" | xargs -r docker rm -f >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

psql_exec() { docker exec -i "$PG" psql -v ON_ERROR_STOP=1 -U postgres -d "$DB" -qAt "$@"; }

echo "== acceptance: control=$CONTROL migration=$MIGRATION previous=${PREVIOUS:-<first run>} budget=${BUDGET}s"
docker network create "$NET" >/dev/null

echo "== postgres ($POSTGRES_IMAGE)"
docker run -d --name "$PG" --network "$NET" \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB="$DB" "$POSTGRES_IMAGE" >/dev/null
for _ in $(seq 1 60); do
  docker exec "$PG" pg_isready -U postgres -d "$DB" >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$PG" pg_isready -U postgres -d "$DB" >/dev/null || { echo "::error::postgres did not accept connections within 60s"; exit 1; }
# pg_isready answers during the image's init restart; a real query is the signal.
for _ in $(seq 1 30); do psql_exec -c 'select 1' >/dev/null 2>&1 && break; sleep 1; done
psql_exec -c 'select 1' >/dev/null

echo "== migration"
MIGLOG="$(mktemp)"
if ! docker run --rm --name "$RUN-migrate" --network "$NET" \
      -e ConnectionStrings__memex="$CONN" "$MIGRATION" >"$MIGLOG" 2>&1; then
  tail -n 80 "$MIGLOG"
  echo "::error::the migration image exited non-zero against an empty database"
  exit 1
fi
if ! grep -q 'Database migration completed' "$MIGLOG"; then
  tail -n 80 "$MIGLOG"
  echo "::error::the migration exited 0 but never logged 'Database migration completed' — refusing to read that as a migrated database"
  exit 1
fi
grep 'Database migration completed' "$MIGLOG" | tail -n 1

# boot_and_wait <leg> <image>: start the image, wait for /health then /ready inside the budget,
# then stop it. The environment is the chart's self-host minimum (MeshWeaver.Testcontainers'
# MemexBuilder): Postgres for nodes, the filesystem backend, single-node Localhost clustering, no
# dev login. Nothing control-specific is passed: the closed type set, the seeded module and the
# disabled sweep are properties of the IMAGE, which is exactly what is under test.
boot_and_wait() {
  local leg="$1" image="$2" name="$RUN-$1" port started status
  echo "== leg $leg: $image"
  # /data is a writable tmpfs, standing in for the chart's data PVC: the image runs as a non-root
  # user and cannot create it (measured: UnauthorizedAccessException on '/data').
  docker run -d --name "$name" --network "$NET" -p 127.0.0.1::8080 \
    --mount type=tmpfs,destination=/data,tmpfs-mode=1777 \
    -e ConnectionStrings__memex="$CONN" \
    -e ASPNETCORE_HTTP_PORTS=8080 \
    -e Graph__Storage__Type=PostgreSql \
    -e Graph__Storage__BasePath=/data/graph \
    -e Deployment__Backend=Filesystem \
    -e Deployment__DataRoot=/data \
    -e Features__Orleans__Clustering=Localhost \
    -e Authentication__EnableDevLogin=false \
    "$image" >/dev/null
  port="$(docker port "$name" 8080/tcp | head -n 1 | sed 's/.*://')"
  started=$(date +%s)
  local phase=/health
  while :; do
    if [ "$(docker inspect -f '{{.State.Running}}' "$name")" != true ]; then
      docker logs --tail 120 "$name" 2>&1 || true
      echo "::error::leg $leg: the container EXITED before it was ready (exit code $(docker inspect -f '{{.State.ExitCode}}' "$name"))"
      return 1
    fi
    status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port$phase" || true)"
    if [ "$status" = 200 ]; then
      if [ "$phase" = /health ]; then phase=/ready; continue; fi
      echo "leg $leg: /health then /ready answered 200 after $(( $(date +%s) - started ))s — now the evidence"
      break
    fi
    if [ $(( $(date +%s) - started )) -ge "$BUDGET" ]; then
      echo "--- /health body:"; curl -s --max-time 5 "http://127.0.0.1:$port/health" || true; echo
      echo "--- log tail:"; docker logs --tail 120 "$name" 2>&1 || true
      echo "::error::leg $leg: not ready within ${BUDGET}s — $phase last answered '$status'"
      return 1
    fi
    sleep 5
  done
  # The evidence, still inside the same budget: the seed runs off the startup path, so wait for its
  # last line rather than reading the log once.
  local log
  while :; do
    log="$(docker logs "$name" 2>&1)"
    if grep -q 'Serving the FIRST-RUN SETUP wizard' <<<"$log"; then
      echo "$log" | tail -n 40
      echo "::error::leg $leg: the process is serving the first-run SETUP wizard — no mesh was composed, so its readiness proves nothing"
      return 1
    fi
    if grep -q '\[FleetControlSeed\] Hosting/Skill/triage: ' <<<"$log"; then break; fi
    if [ $(( $(date +%s) - started )) -ge "$BUDGET" ]; then
      echo "$log" | tail -n 80
      echo "::error::leg $leg: ready, but the control module's seed did not finish within ${BUDGET}s (no '[FleetControlSeed] Hosting/Skill/triage' line)"
      return 1
    fi
    sleep 5
  done
  grep -q '\[ModuleLoad\] MeshWeaver.Fleet.Control ← /app/modules/' <<<"$log" || {
    echo "$log" | grep -F '[ModuleLoad]' || true
    echo "::error::leg $leg: MeshWeaver.Fleet.Control was not loaded from the image's modules/ — this is not a control image"
    return 1; }
  # P1/P2 from the process's own report: the adopt-only boot enumerated ZERO dynamic NodeTypes — in
  # leg (b) that is with a NodeType row present in a user partition, which an open image counts.
  local adopt=""
  while :; do
    adopt="$(docker logs "$name" 2>&1 | grep -o 'ADOPT-ONLY boot complete.*dynamic NodeType(s)' | tail -n 1 || true)"
    [ -n "$adopt" ] && break
    if [ $(( $(date +%s) - started )) -ge "$BUDGET" ]; then
      echo "::error::leg $leg: the pre-warmer never reported its adopt-only boot within ${BUDGET}s — nothing says which dynamic types this process enumerated"
      return 1
    fi
    sleep 5
  done
  grep -q 'of 0 dynamic NodeType(s)' <<<"$adopt" || {
    echo "::error::leg $leg: the process enumerated database NodeTypes ('$adopt') — the type set is NOT closed"
    return 1; }
  if grep -q '\[FleetControlSeed\] .*NOT created' <<<"$log"; then
    echo "$log" | grep -A1 'FleetControlSeed' | tail -n 40
    echo "::error::leg $leg: the control module's seed could not create node(s) — see the NOT created lines above"
    return 1
  fi
  echo "leg $leg: evidence — $adopt"
  echo "leg $leg: evidence — mesh composed; $(grep -c '\[FleetControlSeed\] .*: Created' <<<"$log") seed node(s) created, $(grep -c '\[FleetControlSeed\] .*: AlreadyPresent' <<<"$log") already present; control module loaded from modules/"
  echo "leg $leg: READY after $(( $(date +%s) - started ))s"
  docker rm -f "$name" >/dev/null
}

boot_and_wait a-empty-db "$CONTROL"

echo "== writing a broken NodeType row into a user-shaped partition"
# The partition is created by the platform's OWN provisioning function (byte-faithful to the
# runtime DDL), then listed as searchable — the shape a user's partition has. The row is a NodeType
# whose configuration names a method that does not exist: on an open image it is a compile target
# every enumeration sees; on the closed control image it must change nothing.
psql_exec <<'SQL'
SELECT public.ensure_partition_schema('acceptanceuser');
INSERT INTO public.searchable_schemas (schema_name) VALUES ('acceptanceuser') ON CONFLICT DO NOTHING;
INSERT INTO acceptanceuser.mesh_nodes (namespace, id, name, node_type, state, content, main_node)
VALUES ('', 'AcceptanceUser', 'Acceptance User', 'Markdown', 2,
        '{"$type":"MarkdownContent","content":"A user-shaped partition for the control image acceptance test."}'::jsonb,
        'AcceptanceUser');
INSERT INTO acceptanceuser.mesh_nodes (namespace, id, name, node_type, state, content, main_node)
VALUES ('AcceptanceUser', 'BrokenToggle', 'Broken toggle', 'NodeType', 2,
        '{"$type":"NodeTypeDefinition","description":"deliberately broken: its configuration does not compile","configuration":"config => config.ThisMethodDoesNotExist()"}'::jsonb,
        'AcceptanceUser/BrokenToggle');
SQL
[ "$(psql_exec -c "select count(*) from acceptanceuser.mesh_nodes where node_type='NodeType'")" = 1 ] \
  || { echo "::error::the broken NodeType row is not in place — leg (b) would test nothing"; exit 1; }

boot_and_wait b-broken-nodetype "$CONTROL"
# P1 in the database: a closed image never compiles, adopts or stamps a database NodeType.
stamped="$(psql_exec -c "select coalesce(content->>'compilationStatus','') || coalesce(content->>'latestAssemblyPath','') from acceptanceuser.mesh_nodes where id='BrokenToggle'")"
if [ -n "$stamped" ]; then
  echo "::error::leg b: the broken NodeType row was TOUCHED by the control image (compile/adoption state '$stamped') — the type set is not closed"
  exit 1
fi
echo "leg b-broken-nodetype: the broken row carries no compile or adoption state — never compiled, never adopted"

if [ "$FIRST_RUN" = true ]; then
  echo "::notice::leg (c) not applicable: first control image — no previous memex-control tag exists yet. Every later run must pass --previous."
else
  boot_and_wait c-previous-image-on-migrated-db "$PREVIOUS"
fi

echo "== acceptance PASSED"
