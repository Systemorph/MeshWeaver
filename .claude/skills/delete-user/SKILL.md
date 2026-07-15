---
name: delete-user
description: Remove / reset a user (and their partition) on a running MeshWeaver portal (AKS). Use when you need to kick out a user — e.g. a throwaway test account — so they re-onboard from scratch. The normal delete API REFUSES a user's home ("user partition root … cannot be deleted"), so deletion is a Postgres-schema drop, not an MCP call. Covers where a user's data actually lives (per-user PG schemas named by BOTH id AND email, e.g. `rbuergi` + `rbuergi@systemorph.com`), how to reach PG (the connection string is inline in the portal's `ConnectionStrings__memex` env — NOT the `POSTGRES_PASSWORD` secret), the substring-match footgun (`buergi` also matches admin `rbuergi`), the in-memory resurrection gotcha (a recycle re-saves the node — a portal restart is what actually clears it), and the half-onboarded case where the user has NO schema at all (in-memory only → restart is the whole fix).
user-invocable: true
allowed-tools:
  - Bash
  - Read
---

# /delete-user — remove a user + partition from a running MeshWeaver portal

Deleting a user is **not** an MCP/API operation — the framework guards it. This skill does it via the Postgres layer, safely.

## 1. Why the API refuses

The MCP `delete` (and `DeleteNodeRequest`) **guards user partition roots**:

> `Cannot delete '<user>': '<user>' is a user partition root (home) and cannot be deleted — that would remove the entire partition (threads, tokens, settings, access grants) and lock the user out.`

So you cannot delete a user via `delete @<user>`. You drop their **Postgres schema(s)**.

## 2. Where a user's data lives

`public.mesh_nodes` is empty by design; **each partition is its own PG schema** (`{id}.mesh_nodes` + satellite tables `access`, `threads`, `activities`, …). A user typically has **TWO** schemas — one by **id** and one by **email**:

```
rbuergi                     rbuergi@systemorph.com
mkleiner                    …
```

So dropping `<id>` alone can leave `<id>@<domain>` behind. Drop **both**.

> ⚠️ **Half-onboarded users have NO schema.** If onboarding never provisioned the partition (the user created no content / a write failed), the User node lives **only in the portal's in-memory workspace** — nothing in PG at all. Then there is nothing to drop; step 5 (restart) is the whole fix. Verify with the find in step 4.

## 3. Reach Postgres

The private AKS cluster: `kubectl` only via `az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "…"`.

**The DB password is inline in the portal's `ConnectionStrings__memex` env — NOT the `POSTGRES_PASSWORD` secret** (that secret is a *different, unused* value; using it gives `password authentication failed`). Parse the real one out of the portal env and hand it to a `postgres:16` client pod (the invoke shell has **no `sed`/`tr`/`python3`** — use bash parameter expansion only). Pass SQL to the pod **base64-encoded** to dodge four levels of quoting.

Connection string shape: `Host=10.42.18.4;Port=5432;Username=memexadmin;Password=…;Database=memex;SslMode=Require;…`

## 4. FIND the user's footprint first (never drop blind)

Run this to see the exact schema names + confirm where (if anywhere) the user is persisted. **Match on the full id, and beware: `buergi` matches BOTH `roland.buergi` and admin `rbuergi`** — use the distinguishing part of the id (`roland`, not `buergi`).

```bash
# find_user.sh  — pass the user's DISTINCTIVE id fragment as $1 (e.g. "roland", not "buergi")
NEEDLE="${1:?usage: find_user.sh <distinctive-id-fragment>}"
cat > /tmp/find.sql <<SQL
\echo '== schemas matching =='
SELECT schema_name FROM information_schema.schemata WHERE schema_name ILIKE '%${NEEDLE}%';
DO \$\$ DECLARE r record; c int; BEGIN
  FOR r IN SELECT table_schema FROM information_schema.tables WHERE table_name='mesh_nodes' LOOP
    EXECUTE format('SELECT count(*) FROM %I.mesh_nodes WHERE mesh_nodes::text ILIKE %L', r.table_schema, '%${NEEDLE}%') INTO c;
    IF c>0 THEN RAISE NOTICE 'mesh_nodes HIT schema=% rows=%', r.table_schema, c; END IF;
  END LOOP;
END \$\$;
SQL
run_psql_pod /tmp/find.sql     # see the pod helper below
```

Zero schemas + zero mesh_nodes hits ⇒ half-onboarded (in-memory only) ⇒ skip to step 5.

## 5. DROP the schema(s), then RESTART the portal

Dropping the schema is not enough on its own: the portal's in-memory workspace **resurrects** the node on the next partition reactivation (a `recycle @<user>` disposes the hub but reactivation **re-saves** it — the version bumps and it's back). The reliable clear is a **portal restart** (nothing in PG to reload).

```bash
# 1) drop every schema whose name contains the DISTINCTIVE fragment (both id and email variants)
DROP SCHEMA "roland.buergi" CASCADE;
DROP SCHEMA "roland.buergi@gmail.com" CASCADE;   -- if it exists

# 2) clear the in-memory copy so it can't resurrect
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
  "kubectl -n memex rollout restart deployment/memex-portal-deployment; kubectl -n memex rollout status deployment/memex-portal-deployment --timeout=300s"
```

After the restart, `get @<user>` should be **Not found**, and the user re-onboards on next login.

## The psql-client-pod helper (`run_psql_pod`)

Put this in your script; it pulls the connection from the portal env and runs a base64'd SQL file in a throwaway `postgres:16` pod. The password is **only** ever in the pod env (never printed).

```bash
run_psql_pod() {  # $1 = path to a .sql file
  local NS=memex SQLFILE="$1"
  local CS host user pass db B64
  CS=$(kubectl -n $NS exec deploy/memex-portal-deployment -c memex-portal -- printenv ConnectionStrings__memex 2>/dev/null)
  host=${CS#*Host=};     host=${host%%;*}
  user=${CS#*Username=}; user=${user%%;*}
  pass=${CS#*Password=}; pass=${pass%%;*}
  db=${CS#*Database=};   db=${db%%;*}
  B64=$(base64 < "$SQLFILE" | tr -d '\n')
  kubectl -n $NS delete pod pgc --ignore-not-found >/dev/null 2>&1
  kubectl -n $NS run pgc --image=postgres:16 --restart=Never \
    --env="PGPASSWORD=$pass" \
    --env="CONN=sslmode=require host=$host port=5432 user=$user dbname=$db" \
    --env="B64=$B64" \
    --command -- bash -c 'echo "$B64" | base64 -d > /tmp/q.sql; psql "$CONN" -f /tmp/q.sql 2>&1' >/dev/null 2>&1
  for i in $(seq 1 40); do
    ph=$(kubectl -n $NS get pod pgc -o jsonpath='{.status.phase}' 2>/dev/null || true)
    { [ "$ph" = Succeeded ] || [ "$ph" = Failed ]; } && break; sleep 3
  done
  kubectl -n $NS logs pgc 2>&1
  kubectl -n $NS delete pod pgc --ignore-not-found >/dev/null 2>&1
}
```
Wrap the whole script in a file and invoke it with `az aks command invoke … --command "bash script.sh" --file script.sh` (the `--file` upload avoids inline-quoting hell; the invoke shell has `kubectl` but not `sed`/`tr`/`python3`).

## Checklist

1. Identify the user's **distinctive** id fragment (not a substring shared with admins).
2. **Find** their schemas + mesh_nodes rows (step 4). Confirm you're touching only the right user.
3. `DROP SCHEMA "<id>" CASCADE;` and `DROP SCHEMA "<id>@<domain>" CASCADE;` for each match.
4. **Restart the portal** (step 5) — the in-memory workspace resurrects the node otherwise.
5. Verify `get @<user>` → Not found; the user re-onboards on next login.
