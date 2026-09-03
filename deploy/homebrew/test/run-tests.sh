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
#   FAKE_CURL_FAILS                   — curl cannot connect at all (no port-forward bound)
# ---------------------------------------------------------------------------
STUBS="$(mktemp -d)"
trap 'rm -rf "$STUBS"' EXIT

cat > "$STUBS/colima" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF

# curl is called as: curl [--cacert X] -sS -o /dev/null -w '%{http_code}' --max-time 15 <url>
# 🚨 The FAKE_CURL_FAILS arm emulates the one curl behaviour that matters here and that a
# stub which only ever exits 0 can never reach: on a CONNECTION failure curl still WRITES its
# %{http_code} — the literal '000' — and THEN exits non-zero. probe_http's `|| printf '000'`
# therefore APPENDED a second '000', and the resulting '000000' matched no arm of the caller's
# case statement. That is how "nothing is listening on :8443" got reported as a serving-portal
# fault with the INGRESS as its remedy. The old stub is why this suite never saw it.
# 🚨 The stub must honour the -w FORMAT, not just the URL. awaiting_setup asks for
# %{redirect_url} as well as %{http_code}, and a stub that answered a status code to both made the
# setup branch unreachable — the suite would have gone green over a function it never ran.
cat > "$STUBS/curl" <<'EOF'
#!/usr/bin/env bash
url="${!#}"
fmt=""
prev=""
for a in "$@"; do
  [ "$prev" = "-w" ] && fmt="$a"
  prev="$a"
done
if [ -n "${FAKE_CURL_FAILS:-}" ]; then printf '000'; exit 7; fi
case "$fmt" in
  *redirect_url*)
    # Only an instance awaiting setup redirects the ROOT to the wizard.
    case "$url" in
      *//*/) printf '%s' "${FAKE_REDIRECT_ROOT:-}" ;;
      *)     printf '' ;;
    esac
    ;;
  *)
    case "$url" in
      */login) printf '%s' "${FAKE_HTTP_LOGIN:-200}" ;;
      */setup) printf '%s' "${FAKE_HTTP_SETUP:-200}" ;;
      *)       printf '%s' "${FAKE_HTTP_ROOT:-200}" ;;
    esac
    ;;
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

# 🚨 The single most common red state a developer meets, and the one §14 misnamed. `up` and
# `update` both restart the launchd port-forward and go STRAIGHT into the verification, so the
# probes routinely arrive before :8443 is bound. Real curl writes '000' and exits non-zero, the
# fallback appended a second '000', and '000000' fell through every arm of the case statement to
# the generic "that is not a serving portal" — which names the INGRESS. The developer is then sent
# to `memex-local logs` to read a healthy pod, for a socket that is missing on their own Mac.
reports "a connection failure is 'no answer', not a serving-portal fault" "did NOT answer" \
  FAKE_CURL_FAILS=1 FAKE_MODULES="$BOTH_PRESENT"

run_verify FAKE_CURL_FAILS=1 FAKE_MODULES="$BOTH_PRESENT"
case "$OUT" in
  *000000*) bad "a connection failure reports ONE status, not a doubled one" \
              "reported HTTP 000000 — probe_http concatenated curl's own '000' with its fallback: $OUT" ;;
  *) ok "a connection failure reports ONE status, not a doubled one" ;;
esac
case "$OUT" in
  *port-forward*) ok "a connection failure names the port-forward as the remedy" ;;
  *) bad "a connection failure names the port-forward as the remedy" \
       "nothing is listening on the host; sending the developer at the ingress wastes the session: $OUT" ;;
esac

# 🚨 Check 2 must not diagnose the sign-in ROUTE when nothing answered at all — it cannot know
# anything about /login through a transport that is not there, and "Remedy: memex-local logs"
# sends the developer to a healthy pod for a second time in the same output. One cause must
# produce one diagnosis; the transport is check 1's to name and check 2 defers to it.
case "$OUT" in
  *"/login answered HTTP 000"*) bad "a connection failure does not also indict /login" \
       "check 2 judged the sign-in route through a transport that never connected: $OUT" ;;
  *) ok "a connection failure does not also indict /login" ;;
esac

# ── an instance AWAITING FIRST-RUN SETUP ────────────────────────────────────────
# 🚨 It has no mesh, therefore no view packs, so every check below would condemn a portal that is
# working exactly as designed — a false RED on the first path a new user takes. verify_usable must
# recognise the state and say what to do about it.
# `reports` is for RED states — it demands a non-zero exit. Awaiting setup is a green state with
# an instruction, so it is asserted directly.
run_verify FAKE_HTTP_ROOT=302 FAKE_REDIRECT_ROOT="https://memex.localhost:8443/setup" \
  FAKE_HTTP_SETUP=200 FAKE_MODULES=""
if [ "$RC" -eq 0 ]; then ok "awaiting setup exits 0 — it is a state, not a failure"
else bad "awaiting setup exits 0 — it is a state, not a failure" "exited $RC: $OUT"; fi
case "$OUT" in
  *"AWAITING FIRST-RUN SETUP"*) ok "awaiting setup is reported, not condemned" ;;
  *) bad "awaiting setup is reported, not condemned" "said nothing about setup: $OUT" ;;
esac
case "$OUT" in
  *"memex-local setup"*) ok "awaiting setup points at the command that answers it" ;;
  *) bad "awaiting setup points at the command that answers it" "named no next step: $OUT" ;;
esac

# 🚨 THE NEGATIVE CONTROL, and it is not hypothetical: the first draft of this probe asked only
# "does /setup answer 200", and a CONFIGURED portal answers 200 there too — its Blazor SPA fallback
# serves almost any path. Every red state below then short-circuited into "awaiting setup" and the
# whole verification passed on a broken portal. The root REDIRECT is what discriminates.
run_verify FAKE_HTTP_ROOT=200 FAKE_HTTP_SETUP=200 FAKE_MODULES=""
case "$OUT" in
  *"AWAITING FIRST-RUN SETUP"*)
    bad "a configured portal is NOT mistaken for one awaiting setup" \
        "/setup answering 200 through the SPA fallback was read as setup mode: $OUT" ;;
  *) ok "a configured portal is NOT mistaken for one awaiting setup" ;;
esac
if [ "$RC" -ne 0 ]; then ok "…and its missing view packs are still caught"
else bad "…and its missing view packs are still caught" "exited 0 on a portal with no view packs: $OUT"; fi

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
# The safe form ${x[@]+"${x[@]}"} CONTAINS the unsafe spelling, so lines carrying the guard marker
# `[@]+"` are dropped along with whole-line comments, and whatever still matches is unguarded. (A
# line holding a guarded AND an unguarded expansion would slip through; grep beats a portable-regex
# argument with sed for a check that has to behave identically on BSD and GNU. "$@" is a special
# parameter, not an array, and is always safe.)
unsafe="$(grep -vE '^[[:space:]]*#' "$CLI" \
  | grep -v '\[@\]+"' \
  | grep -E '"\$\{[A-Za-z_][A-Za-z0-9_]*\[@\]\}"' || true)"
if [ -z "$unsafe" ]; then ok 'no unguarded "${array[@]}" (bash 3.2 + set -u would abort)'
else bad 'no unguarded "${array[@]}"' "use \${x[@]+\"\${x[@]}\"} on: ${unsafe}"; fi

echo "── up/update actually RUN the verification ───────────────────────"

# The whole point of MeshWeaver#2367 is that these two reported success on an unusable portal.
# Deleting the call is the easy regression, and it leaves no trace anywhere else.
for c in cmd_up cmd_update; do
  # No `{` in the pattern: it opens an interval in ERE, and awk implementations disagree about
  # whether `\{` is an error, a warning or a literal. `^cmd_up\(\)` is unambiguous everywhere.
  body="$(awk -v f="^${c}\\\\(\\\\)" '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
  case "$body" in
    *"verify_usable --wait"*) ok "${c} runs the usability verification" ;;
    *) bad "${c} runs the usability verification" "no verify_usable call in ${c}()" ;;
  esac
done

echo "── a gate's own INPUT must be reachable (static — needs a live pod) ──"

# 🚨 §14 decides the boot reconcile has SETTLED by grepping the pod log for
# `[DefaultInstall] reconciled`. That call is LogInformation on a MeshWeaver.PluginCatalog.*
# category, and the portal image's appsettings.json caps the whole `MeshWeaver` prefix at Warning
# — so the line never reaches stdout on ANY install. The wait could therefore never settle early:
# it burned its full --wait budget (600 s on `update`) and then reported "the boot reconcile has
# not reported completion", which is a statement about the LOGGING CONFIG, not about the install.
# apply_logging set only Logging__LogLevel__Default, and a Default override does not lift a more
# specific prefix. Same defect class as the unreadable pod above: the check could not read its own
# input. Asserted statically — proving it dynamically needs a running portal.
body="$(awk -v f='^apply_logging\\(\\)' '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
case "$body" in
  *Logging__LogLevel__MeshWeaver.PluginCatalog=Information*)
    ok "apply_logging lifts the category the DefaultInstall probe greps" ;;
  *) bad "apply_logging lifts the category the DefaultInstall probe greps" \
       "appsettings caps 'MeshWeaver' at Warning, so [DefaultInstall] reconciled is suppressed and the wait can never settle: ${body}" ;;
esac

# 🚨 `launchctl bootstrap` returns when launchd has ACCEPTED the job, not when the kubectl
# port-forward inside it has bound the socket. Both cmd_up and cmd_update go straight from
# install_launchd into verify_usable, so the verification routinely probes a socket that does not
# exist yet and reports a portal that is in fact fine. Waiting on the port is waiting on the
# actual precondition — and any HTTP status at all, 503 included, proves the socket is bound and
# is check 1's business to judge, so the wait cannot mask a genuinely broken portal.
body="$(awk -v f='^install_launchd\\(\\)' '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
case "$body" in
  *wait_port_forward*) ok "install_launchd waits for the socket to actually bind" ;;
  *) bad "install_launchd waits for the socket to actually bind" \
       "it returns as soon as launchd accepts the job, and every caller verifies immediately after: ${body}" ;;
esac

echo "── registry mode is a state machine over ONE file ────────────────"

# `memex-local registry` decides where a local install gets its plugins from — its own checkout
# (which can never land a module binary, MeshWeaver#2417) or a remote registry — by writing or
# removing ~/.memex-local/registry.yaml. Driven through the whole cycle against a scratch home; a
# secret that is printed back, a wrong-shaped key that is accepted, or a file readable by everyone
# are each a defect this suite exists to catch.
RHOME="$STUBS/home"
reg() { OUT="$(env MEMEX_LOCAL_HOME="$RHOME" "$CLI" registry "$@" 2>&1)"; RC=$?; }

reg status
if [ "$RC" -eq 0 ] && case "$OUT" in *SELF-REGISTRY*) true ;; *) false ;; esac; then
  ok "a fresh install reports self-registry mode"
else bad "a fresh install reports self-registry mode" "exited ${RC}: ${OUT}"; fi

# No key is the DEFAULT: an open registration the registry enrols into its default plan (the free
# tier). The file must then carry NO secrets block at all — an empty PluginCatalog__BootstrapKey
# would still render into the chart's secret and read as "a key was given".
reg https://memex.example.test --id open-box
rfile="$RHOME/registry.yaml"
if [ "$RC" -eq 0 ] && case "$OUT" in *"OPEN registration"*) true ;; *) false ;; esac; then
  ok "a registry URL without a key is an OPEN (free-tier) registration"
else bad "a registry URL without a key is an OPEN (free-tier) registration" "exited ${RC}: ${OUT}"; fi
if [ -f "$rfile" ] && ! grep -q "PluginCatalog__BootstrapKey\|^secrets:" "$rfile"; then
  ok "an open registration writes no secrets block"
else bad "an open registration writes no secrets block" "$(cat "$rfile" 2>/dev/null)"; fi
reg status
case "$OUT" in *"OPEN"*"free tier"*) ok "status names the open registration and the free tier" ;;
  *) bad "status names the open registration and the free tier" "$OUT" ;; esac
reg off >/dev/null 2>&1 || true

reg https://memex.example.test --key mwi_this-is-an-instance-key
if [ "$RC" -ne 0 ] && case "$OUT" in *"REGISTRATION key"*) true ;; *) false ;; esac; then
  ok "an instance key (mwi_) is refused by name, not by a 401 on first boot"
else bad "an instance key (mwi_) is refused by name" "exited ${RC}: ${OUT}"; fi

reg not-a-url --key mwr_x
if [ "$RC" -ne 0 ] && case "$OUT" in *"expected a URL"*) true ;; *) false ;; esac; then
  ok "a non-URL first argument refuses"
else bad "a non-URL first argument refuses" "exited ${RC}: ${OUT}"; fi

reg https://memex.example.test --key mwr_x --id "Not Valid"
if [ "$RC" -ne 0 ] && case "$OUT" in *"--id must be"*) true ;; *) false ;; esac; then
  ok "an instance id outside the registry's alphabet refuses"
else bad "an instance id outside the registry's alphabet refuses" "exited ${RC}: ${OUT}"; fi

reg https://memex.example.test/ --key mwr_abcdefghijklmnop --id my-box
if [ "$RC" -eq 0 ] && [ -f "$rfile" ]; then ok "registry <url> --key --id writes the registry file"
else bad "registry <url> --key --id writes the registry file" "exited ${RC}: ${OUT}"; fi
if grep -q 'registryUrl: "https://memex.example.test"$' "$rfile" 2>/dev/null; then
  ok "the URL is stored without its trailing slash"
else bad "the URL is stored without its trailing slash" "$(cat "$rfile" 2>/dev/null)"; fi
if grep -q 'instanceId: "my-box"$' "$rfile" 2>/dev/null \
   && grep -q 'PluginCatalog__BootstrapKey: "mwr_abcdefghijklmnop"$' "$rfile" 2>/dev/null \
   && grep -q '^pluginCatalog:$' "$rfile" 2>/dev/null && grep -q '^secrets:$' "$rfile" 2>/dev/null; then
  ok "the file carries pluginCatalog.{registryUrl,instanceId} and the bootstrap-key secret"
else bad "the file carries pluginCatalog.{registryUrl,instanceId} and the bootstrap-key secret" "$(cat "$rfile" 2>/dev/null)"; fi
case "$(ls -l "$rfile" 2>/dev/null | cut -c1-10)" in
  -rw-------) ok "the registry file is 0600 (it holds a secret)" ;;
  *) bad "the registry file is 0600 (it holds a secret)" "$(ls -l "$rfile" 2>/dev/null)" ;;
esac

reg status
if [ "$RC" -eq 0 ] && case "$OUT" in *"REGISTRY MODE"*"my-box"*) true ;; *) false ;; esac; then
  ok "status reports registry mode and the instance id"
else bad "status reports registry mode and the instance id" "exited ${RC}: ${OUT}"; fi
case "$OUT" in
  *mwr_abcdefghijklmnop*) bad "status never prints the bootstrap key" "$OUT" ;;
  *) ok "status never prints the bootstrap key" ;;
esac

# In registry mode a missing view pack must send the developer to the REGISTRY (grants, the key),
# not to a plugins checkout this install no longer mounts.
reports "in registry mode a missing pack names the registry, not a checkout" "memex.example.test" \
  MEMEX_LOCAL_HOME="$RHOME" FAKE_HTTP_ROOT=200 FAKE_HTTP_LOGIN=200 \
  FAKE_MODULES="module=MeshWeaver.Blazor.Views missing"

reg off
if [ "$RC" -eq 0 ] && [ ! -f "$rfile" ]; then ok "registry off removes the file"
else bad "registry off removes the file" "exited ${RC}: ${OUT}"; fi
reg status
case "$OUT" in *SELF-REGISTRY*) ok "after registry off the install is back in self-registry mode" ;;
  *) bad "after registry off the install is back in self-registry mode" "$OUT" ;; esac

reg --bogus
if [ "$RC" -ne 0 ]; then ok "an unknown registry argument refuses"
else bad "an unknown registry argument refuses" "exited 0: $OUT"; fi

# Static: the two halves of the mode switch. helm_deploy must layer the registry file in one mode
# and the self-registry blanking layer in the other; up/update must default to the CI-built image
# in registry mode — a source build there would run a portal the registry never baked against.
body="$(awk -v f='^helm_deploy\\(\\)' '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
case "$body" in
  *'-f "$REGISTRY_FILE"'*"values.local.self-registry.yaml"*)
    ok "helm_deploy layers the registry file OR the self-registry defaults, by mode" ;;
  *) bad "helm_deploy layers the registry file OR the self-registry defaults, by mode" "$body" ;;
esac
for c in cmd_up cmd_update; do
  body="$(awk -v f="^${c}\\\\(\\\\)" '$0 ~ f { on=1 } on { print } on && /^}$/ { exit }' "$CLI")"
  case "$body" in
    *'registry_mode && image_source="acr"'*) ok "${c} defaults to the ACR image in registry mode" ;;
    *) bad "${c} defaults to the ACR image in registry mode" "no registry-mode default in ${c}()" ;;
  esac
done

echo "── the CLI still knows every command it documents ────────────────"

help_out="$("$CLI" help 2>&1)"
for cmd in up down status setup logs update registry verify port-forward observability doctor; do
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
