#!/usr/bin/env bash
# sandbox-postgres.sh — a DISPOSABLE PostgreSQL server for ONE build-host stage (Doc/Architecture/BuildHosts).
#
#   eval "$(/opt/build-host/sandbox-postgres.sh)"      # inside a stage; then PGHOST/PGUSER/PGPASSWORD
#                                                       # and MW_BUILD_POSTGRES (an Npgsql string) are set
#   sandbox-postgres.sh --self-test
#
# There is NO shared database on a build host. A stage that needs one starts its own, INSIDE its
# bwrap sandbox:
#   * the data directory is on the stage's tmpfs scratch ($TMPDIR — /scratch/tmp), gone with it;
#   * the password is random per stage (24 bytes, never on disk after initdb read it);
#   * it listens on a Unix socket in that directory ONLY — `listen_addresses=''`, no TCP at all, so
#     nothing else on the pod's network namespace can reach it;
#   * it dies with the stage: the server is a descendant of the stage's process tree, and the
#     sandbox's PID namespace + --die-with-parent end every process in it when the stage ends.
#
# Prints the `export` line on stdout; everything else goes to stderr. Fails closed: no server
# binaries, an initdb or a start that fails, is exit 1 naming it.
set -euo pipefail

if [ "${1:-}" = "--self-test" ]; then
  # The parts that can be proved without a server: the start options carry no TCP listener and a
  # private socket, and a missing server is a named failure.
  self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  grep -q "listen_addresses=''" "$self" || { echo "SELF-TEST FAILED: no listen_addresses=''"; exit 1; }
  grep -q "unix_socket_permissions=0700" "$self" || { echo "SELF-TEST FAILED: the socket is not private"; exit 1; }
  if PG_BIN_DIR=/nonexistent "$self" >/dev/null 2>&1; then echo "SELF-TEST FAILED: no server binaries was not refused"; exit 1; fi
  echo "sandbox-postgres self-test: passed (no TCP, private socket, missing binaries refused)"
  exit 0
fi

bin="${PG_BIN_DIR:-}"
if [ -z "$bin" ] && [ -d /usr/lib/postgresql ]; then
  bin="$(find /usr/lib/postgresql -mindepth 2 -maxdepth 2 -type d -name bin | sort -V | tail -n 1)"
fi
[ -n "$bin" ] && [ -x "$bin/initdb" ] && [ -x "$bin/pg_ctl" ] \
  || { echo "sandbox-postgres: no PostgreSQL server binaries (${bin:-/usr/lib/postgresql/*/bin})" >&2; exit 1; }

root="${MW_BUILD_PG_ROOT:-${TMPDIR:-/tmp}/pg-$$}"
mkdir -p "$root"
chmod 0700 "$root"
pw="$(python3 -c 'import secrets; print(secrets.token_hex(24))')"
pwfile="$root/.pw"
( umask 077; printf '%s' "$pw" > "$pwfile" )
"$bin/initdb" -D "$root/data" -U build -A scram-sha-256 --pwfile="$pwfile" >"$root/initdb.log" 2>&1 \
  || { rm -f "$pwfile"; echo "sandbox-postgres: initdb failed:" >&2; tail -n 20 "$root/initdb.log" >&2; exit 1; }
rm -f "$pwfile"
"$bin/pg_ctl" -D "$root/data" -l "$root/server.log" -w -t 60 \
  -o "-c listen_addresses='' -k $root -c unix_socket_permissions=0700 -c fsync=off -c full_page_writes=off" start >/dev/null \
  || { echo "sandbox-postgres: the server did not start:" >&2; tail -n 20 "$root/server.log" >&2; exit 1; }
echo "sandbox-postgres: server up on the Unix socket in $root (no TCP)" >&2
printf "export PGHOST='%s' PGUSER=build PGPASSWORD='%s' PGDATABASE=postgres MW_BUILD_POSTGRES='Host=%s;Username=build;Password=%s;Database=postgres'\n" \
  "$root" "$pw" "$root" "$pw"
