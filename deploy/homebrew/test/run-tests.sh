#!/usr/bin/env bash
# Tests for memex-local — the local-stack CLI every developer's setup runs.
#
# 🚨 WHY THIS EXISTS. deploy/homebrew/bin/memex-local is ~2000 lines that stand up the whole local
# portal, and until MeshWeaver#2367 NOTHING in CI opened it: no shellcheck, no parse, no behaviour.
# The bug that issue reported was a verification that could not fail — `verify_endpoint` curl'd `/`
# and printed `ok "Portal reachable"` whenever curl exited 0, so a 503, a 404 and a portal rendering
# every control as its `ToString()` all produced the same green line. A gate that cannot fail is not
# a gate, and the only way to know a gate can fail is to make it fail.
#
# WHAT THIS CAN AND CANNOT PROVE, stated plainly so a green run is not read as more than it is.
# memex-local drives colima, docker, helm, kubectl, mkcert and curl against a live Colima VM; a CI
# runner has none of those, and mocking them would only assert that the mocks agree with themselves.
# So this covers the layer that IS testable and is where this class of bug lives:
#
#   • the usability verification — every red state it must report, and that it reports each one
#     with a REMEDY rather than a bare failure
#   • argument handling — an unknown flag must refuse, not be silently ignored
#   • that a green run is actually reachable, so the tests above are not passing vacuously
#
# What it does NOT cover: whether `kubectl exec` finds the module directory on a real pod. That is
# settled by a real `memex-local up`, which is why the probe's shape is kept identical to the
# diagnostic commands in MeshWeaver#2367's own repro.

set -uo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CLI="$HERE/../bin/memex-local"

pass=0; fail=0; RC=0
ok()  { printf '  ok    %s\n' "$1"; pass=$((pass+1)); }
bad() { printf '  FAIL  %s\n' "$1"; printf '        %s\n' "${2:-}"; fail=$((fail+1)); }

# ---------------------------------------------------------------------------
# A fake toolchain. Each stub answers from an env var, so one test = one state.
#   FAKE_HTTP_ROOT  / FAKE_HTTP_LOGIN — the status code curl reports per path
#   FAKE_MODULES                      — the lines the in-pod probe prints
#   FAKE_EXEC_FAILS                   — kubectl exec exits non-zero (unreadable pod)
# ---------------------------------------------------------------------------
STUBS="$(mktemp -d)"
trap 'rm -rf "$STUBS"' EXIT

cat > "$STUBS/colima" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF

# curl is called as: curl [--cacert X] -sS -o /dev/null -w '%{http_code}' --max-time 15 <url>
cat > "$STUBS/curl" <<'EOF'
#!/usr/bin/env bash
url="${!#}"
case "$url" in
  */login) printf '%s' "${FAKE_HTTP_LOGIN:-200}" ;;
  *)       printf '%s' "${FAKE_HTTP_ROOT:-200}" ;;
esac
EOF

# kubectl exec — the in-pod module probe. Everything else is a no-op success.
cat > "$STUBS/kubectl" <<'EOF'
#!/usr/bin/env bash
for a in "$@"; do
  if [ "$a" = "exec" ]; then
    [ -n "${FAKE_EXEC_FAILS:-}" ] && exit 1
    printf '%s\n' "${FAKE_MODULES:-}"
    exit 0
  fi
done
exit 0
EOF

chmod +x "$STUBS"/colima "$STUBS"/curl "$STUBS"/kubectl
export PATH="$STUBS:$PATH"

BOTH_PRESENT='module=MeshWeaver.Blazor.Views present
module=MeshWeaver.Blazor.Graph present'

# Run `memex-local verify [$VERIFY_FLAGS]` in a given state, leaving output in OUT and status in RC.
# 🚨 Not `OUT=$(run_verify …)`: a command substitution is a SUBSHELL, so an RC assigned inside one
# never reaches the caller — the first draft of this file did exactly that and every assertion read
# a stale RC=0, i.e. the suite reported "exited 0" for runs that had exited 1. A test harness that
# cannot see the exit code it is asserting on is the same defect as the gate it is testing.
OUT=""
VERIFY_FLAGS=""
run_verify() {
  # shellcheck disable=SC2086  # VERIFY_FLAGS is a deliberate word-split of flags under test
  OUT="$(env "$@" "$CLI" verify $VERIFY_FLAGS 2>&1)"; RC=$?
}

# A red state must EXIT NON-ZERO and SAY WHAT TO DO. "It failed" without a remedy sends the
# developer to the source; that is the whole complaint in MeshWeaver#2367.
reports() {
  local what="$1" phrase="$2"; shift 2
  local out
  run_verify "$@"; out="$OUT"
  if [ "$RC" -eq 0 ]; then bad "$what" "exited 0 — it should have reported a problem. Said: $out"; return; fi
  case "$out" in
    *"$phrase"*) ;;
    *) bad "$what" "failed, but never said '${phrase}'. Said: ${out}"; return ;;
  esac
  case "$out" in
    *Remedy:*) ok "$what" ;;
    *) bad "$what" "reported the problem but named no remedy: ${out}" ;;
  esac
}

echo "── the verification can FAIL (each red state, with a remedy) ─────"

reports "a 503 portal is not 'reachable'" "HTTP 503" \
  FAKE_HTTP_ROOT=503 FAKE_HTTP_LOGIN=503 FAKE_MODULES="$BOTH_PRESENT"

reports "no answer at all is reported" "did NOT answer" \
  FAKE_HTTP_ROOT=000 FAKE_HTTP_LOGIN=000 FAKE_MODULES="$BOTH_PRESENT"

reports "a deleted login page is caught" "NOT ROUTED" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=404 FAKE_MODULES="$BOTH_PRESENT"

reports "a missing view pack is caught" "MeshWeaver.Blazor.Views" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 \
  FAKE_MODULES='module=MeshWeaver.Blazor.Views missing
module=MeshWeaver.Blazor.Graph present'

reports "both view packs missing is caught" "ToString()" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 \
  FAKE_MODULES='module=MeshWeaver.Blazor.Views missing
module=MeshWeaver.Blazor.Graph missing'

reports "landed-but-pending-restart is not green" "restart is PENDING" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 \
  FAKE_MODULES="$BOTH_PRESENT
pending-restart=yes"

# 🚨 The check that cannot read its own input must FAIL, never pass quietly. An unreadable pod
# used to be indistinguishable from a healthy one, which is the skip-trapdoor shape AGENTS.md bans.
reports "an unreadable pod fails the check, not passes it" "verified NOTHING" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 FAKE_EXEC_FAILS=1

# 🚨 "Still installing" must read differently from "will never arrive" — and must still not be a
# success, because the portal cannot render at the moment the command returns. With a wait budget
# and no `[DefaultInstall] reconciled` line in the log, that is the state, and the message has to
# say so rather than accusing the install of having failed.
VERIFY_FLAGS="--wait 1"
reports "an install still in progress says so, and is still not green" "still installing" \
  FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 \
  FAKE_MODULES='module=MeshWeaver.Blazor.Views missing
module=MeshWeaver.Blazor.Graph missing'
VERIFY_FLAGS=""

echo "── …and a green run is reachable (so the above is not vacuous) ───"

run_verify FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 FAKE_MODULES="$BOTH_PRESENT"
if [ "$RC" -ne 0 ]; then
  bad "a healthy portal verifies green" "exited ${RC}: ${OUT}"
elif case "$OUT" in *"view packs present"*) false ;; *) true ;; esac; then
  bad "a healthy portal verifies green" "no 'view packs present' line in: ${OUT}"
else
  ok "a healthy portal verifies green"
fi

# A 302 to the sign-in page is the NORMAL anonymous response — treating it as red would make the
# gate fire on every healthy install and get it deleted within a day.
run_verify FAKE_HTTP_ROOT=302 FAKE_HTTP_LOGIN=200 FAKE_MODULES="$BOTH_PRESENT"
if [ "$RC" -eq 0 ]; then ok "a 302 to sign-in is healthy, not a failure"
else bad "a 302 to sign-in is healthy, not a failure" "exited ${RC}: ${OUT}"; fi

echo "── argument handling ─────────────────────────────────────────────"

out="$(env FAKE_MODULES="$BOTH_PRESENT" "$CLI" verify --nope 2>&1)"; rc=$?
if [ "$rc" -eq 0 ]; then bad "an unknown verify flag refuses" "exited 0: $out"
else case "$out" in *"unknown flag"*) ok "an unknown verify flag refuses" ;;
     *) bad "an unknown verify flag refuses" "refused without saying why: $out" ;; esac; fi

out="$(env FAKE_MODULES="$BOTH_PRESENT" "$CLI" verify --wait forever 2>&1)"; rc=$?
if [ "$rc" -eq 0 ]; then bad "--wait must be numeric" "exited 0: $out"
else case "$out" in *"whole seconds"*) ok "--wait must be numeric" ;;
     *) bad "--wait must be numeric" "refused without saying why: $out" ;; esac; fi

echo "── the help text is TEXT, not code ───────────────────────────────"

# 🚨 `cmd_help` is `cat <<EOF` — an UNQUOTED heredoc, so a backtick in the prose is COMMAND
# SUBSTITUTION. The usage text mentioned the autoroll `main` tag, and `main` is this script's own
# entry function: `memex-local help` re-entered main → cmd_help → main → … forking on every level.
# `memex-local help` was an unbounded fork bomb on main until MeshWeaver#2367, and nothing noticed
# because nothing in CI ever ran this script.
#
# Asserted STATICALLY, not by running `help` and waiting: a regression here HANGS, and a hang is not
# a failure signal — a suite that waits for it just times out with nothing to read. (The delimiter
# cannot simply be quoted: the text interpolates ${HOSTNAME_LOCAL}, ${HOST_PORT}, ${E2E_PORT}.)
heredoc="$(awk '/^  cat <<EOF$/ { on=1; next } on && /^EOF$/ { exit } on' "$CLI")"
bare="$(printf '%s\n' "$heredoc" | grep -c '[^\\]`' || true)"
if [ "$bare" -eq 0 ]; then ok "no unescaped backtick in the help heredoc (it would re-enter main())"
else bad "no unescaped backtick in the help heredoc" \
  "${bare} line(s) run a command instead of printing it: $(printf '%s\n' "$heredoc" | grep -n '[^\\]`')"; fi

echo "── macOS bash 3.2 safety (the runner is bash 5 — assert it statically) ──"

# 🚨 This tool runs ONLY on macOS, whose /bin/bash is 3.2, where `set -u` treats "${x[@]}" on an
# EMPTY array as an unbound variable and kills the script. CI runs bash 5, which tolerates it — so a
# runner can never reach this by executing the code, and the assertion has to be static or it does
# not exist. It was not theoretical: helm_deploy expanded next_args/plugin_args unguarded, both of
# which are empty on the paths the script itself calls normal (--from-acr, no plugin checkout), so
# the deploy died on "unbound variable" for them.
# The safe form is ${x[@]+"${x[@]}"} — which CONTAINS the unsafe spelling, so the guarded form and
# whole-line comments are removed before looking for what is left. ("$@" is a special parameter, not
# an array, and is always safe.)
unsafe="$(grep -vE '^[[:space:]]*#' "$CLI" \
  | sed 's/\${[A-Za-z_][A-Za-z0-9_]*\[@\]+"\${[A-Za-z_][A-Za-z0-9_]*\[@\]}"}//g' \
  | grep -nE '"\$\{[A-Za-z_][A-Za-z0-9_]*\[@\]\}"' || true)"
if [ -z "$unsafe" ]; then ok 'no unguarded "${array[@]}" (bash 3.2 + set -u would abort)'
else bad 'no unguarded "${array[@]}"' "use \${x[@]+\"\${x[@]}\"} at: ${unsafe}"; fi

echo "── up/update actually RUN the verification ───────────────────────"

# The whole point of MeshWeaver#2367 is that these two reported success on an unusable portal.
# Deleting the call is the easy regression, and it leaves no trace anywhere else.
for c in cmd_up cmd_update; do
  body="$(awk -v f="^${c}\\\\(\\\\) \\\\{" '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
  case "$body" in
    *"verify_usable --wait"*) ok "${c} runs the usability verification" ;;
    *) bad "${c} runs the usability verification" "no verify_usable call in ${c}()" ;;
  esac
done

echo "── the CLI still knows every command it documents ────────────────"

help_out="$("$CLI" help 2>&1)"
for cmd in up down status logs update verify port-forward observability doctor; do
  case "$help_out" in
    *"$cmd"*) ;;
    *) bad "help documents '$cmd'" "not in the usage text"; continue ;;
  esac
  if grep -q "^    ${cmd})" "$CLI"; then ok "help documents '$cmd', and main dispatches it"
  else bad "help documents '$cmd'" "no dispatch arm in main()"; fi
done

echo
printf 'passed %d, failed %d\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
