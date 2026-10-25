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
  # The options are checked where they MATTER — on the command line the server is started with —
  # by running this script against STUB server binaries that record their arguments. A test that
  # grepped this file for the strings would stay green with the options removed from pg_ctl.
  self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  st="$(mktemp -d)"
  trap 'rm -rf "$st"' EXIT
  mkdir -p "$st/bin"
  cat > "$st/bin/initdb" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "${STUB_LOG:?}/initdb.args"
while [ $# -gt 0 ]; do case "$1" in -D) mkdir -p "$2"; shift ;; esac; shift; done
STUB
  cat > "$st/bin/pg_ctl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "${STUB_LOG:?}/pg_ctl.args"
STUB
  chmod +x "$st/bin/initdb" "$st/bin/pg_ctl"
  fail() { echo "SELF-TEST FAILED: $1"; exit 1; }
  out="$(STUB_LOG="$st" PG_BIN_DIR="$st/bin" MW_BUILD_PG_ROOT="$st/pg" bash "$self" 2>/dev/null)" || fail "the stubbed start failed"
  opts="$(grep -A1 '^-o$' "$st/pg_ctl.args" | tail -n 1)"
  case "$opts" in *"listen_addresses=''"*) ;; *) fail "the server is started WITH a TCP listener (no listen_addresses='' in: $opts)" ;; esac
  case "$opts" in *"unix_socket_permissions=0700"*) ;; *) fail "the server's socket is not private (no unix_socket_permissions=0700 in: $opts)" ;; esac
  case "$opts" in *"-k $st/pg"*) ;; *) fail "the socket is not in the stage's own directory (no -k $st/pg in: $opts)" ;; esac
  grep -qx 'scram-sha-256' "$st/initdb.args" || fail "initdb does not require a password (no -A scram-sha-256)"
  [ ! -e "$st/pg/.pw" ] || fail "the password file outlives initdb"
  case "$out" in *"PGHOST='$st/pg'"*"MW_BUILD_POSTGRES='Host=$st/pg;"*) ;; *) fail "the export line does not name the socket directory: $out" ;; esac
  pw1="$(printf '%s' "$out" | sed -n "s/.*PGPASSWORD='\([^']*\)'.*/\1/p")"
  out2="$(STUB_LOG="$st" PG_BIN_DIR="$st/bin" MW_BUILD_PG_ROOT="$st/pg2" bash "$self" 2>/dev/null)" || fail "the second stubbed start failed"
  pw2="$(printf '%s' "$out2" | sed -n "s/.*PGPASSWORD='\([^']*\)'.*/\1/p")"
  [ "${#pw1}" -ge 32 ] && [ "$pw1" != "$pw2" ] || fail "the password is not random per stage"
  if PG_BIN_DIR=/nonexistent bash "$self" >/dev/null 2>&1; then fail "no server binaries was not refused"; fi
  echo "sandbox-postgres self-test: passed (the started server has no TCP listener and a private socket in its own dir, scram passwords random per stage, missing binaries refused)"
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
