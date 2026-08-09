#!/usr/bin/env bash
# One-shot prod DB migration: dump portal.example.com's prod Postgres
# (<prod-pg-server>, Entra-auth) and restore into the `exampledb`
# database on the shared <pg-server> Flexible Server.
#
# Runs a postgres:16 pod inside the cluster (the only place with line-of-sight to
# BOTH the prod PG public endpoint — via the AKS egress IP, firewall-allowed — and
# the private <pg-server> at <pg-private-ip>). Reads:
#   TOKEN = an AAD access token for an Entra admin on the prod PG (<entra-admin@example.com>)
#   PW    = the <pg-server> `memexadmin` password
# both provided as env on the `az aks command invoke` that runs this file.
set -uo pipefail
: "${TOKEN:?set TOKEN to a prod-PG AAD access token}"
: "${PW:?set PW to the <pg-server> memexadmin password}"

kubectl -n default delete pod pgmig --ignore-not-found >/dev/null 2>&1
kubectl -n default delete secret pgmig-creds --ignore-not-found >/dev/null 2>&1
kubectl -n default create secret generic pgmig-creds \
  --from-literal=TOKEN="$TOKEN" --from-literal=PW="$PW" >/dev/null

cat > /tmp/pgmig.yaml <<'YAML'
apiVersion: v1
kind: Pod
metadata: { name: pgmig, namespace: default }
spec:
  restartPolicy: Never
  containers:
    - name: pgmig
      image: postgres:16
      command: ["bash","-c"]
      args:
        - |
          set -uo pipefail
          export PGSSLMODE=require
          echo "== dump prod (memex) =="
          PGPASSWORD="$TOKEN" pg_dump --no-owner --no-acl --verbose \
            -h <prod-pg-server>.postgres.database.azure.com \
            -U '<entra-admin@example.com>' -d memex -f /tmp/d.sql 2>/tmp/dump.err
          echo "DUMP_EXIT=$?  bytes=$(wc -c </tmp/d.sql 2>/dev/null)"
          tail -3 /tmp/dump.err
          echo "== restore -> exampledb =="
          PGPASSWORD="$PW" psql -v ON_ERROR_STOP=0 \
            -h <pg-private-ip> -U memexadmin -d exampledb -f /tmp/d.sql >/tmp/r.log 2>&1
          echo "RESTORE_PSQL_EXIT=$?"
          echo "-- restore errors (if any) --"
          grep -iE 'ERROR|FATAL' /tmp/r.log | grep -viE 'already exists|does not exist, skipping' | head -20 || true
          echo "-- table counts in exampledb --"
          PGPASSWORD="$PW" psql -h <pg-private-ip> -U memexadmin -d exampledb -tAc \
            "select count(*) || ' tables' from information_schema.tables where table_schema not in ('pg_catalog','information_schema')" 2>&1
          echo "DONE"
      env:
        - { name: TOKEN, valueFrom: { secretKeyRef: { name: pgmig-creds, key: TOKEN } } }
        - { name: PW,    valueFrom: { secretKeyRef: { name: pgmig-creds, key: PW } } }
YAML

kubectl apply -f /tmp/pgmig.yaml >/dev/null
echo "pgmig pod created; waiting for completion (up to 10m)..."
kubectl -n default wait --for=jsonpath='{.status.phase}'=Succeeded pod/pgmig --timeout=600s 2>&1 || true
echo "===== pgmig logs ====="
kubectl -n default logs pgmig --tail=60
echo "===== phase ====="
kubectl -n default get pod pgmig -o jsonpath='{.status.phase}'; echo
kubectl -n default delete secret pgmig-creds --ignore-not-found >/dev/null 2>&1
