# shellcheck shell=bash
# Shared harness for the hosting-* operator commands. Sourced, never executed.
#
# 🚨 THE ONE RULE THIS FILE EXISTS TO ENFORCE: a step that could not do its job must EXIT NON-ZERO,
# and a step that verifies something must say so only after reading the thing back. run.sh stops at
# the first failure, and the mesh reads `::hosting::` lines out of the pod log — so a script that
# swallows an error does not produce a warning, it produces a green teardown of a live instance.

set -uo pipefail

# The prefix the mesh's OperatorOutput parser reads. Anything else on stdout is human output.
HOSTING_MARKER="::hosting::"

# A machine-readable fact for the mesh: hosting::say size 12345  ->  "::hosting:: size=12345"
hosting::say() { printf '%s %s=%s\n' "$HOSTING_MARKER" "$1" "$2"; }

# Announce the step being entered. run.sh emits this; a script may emit sub-steps.
hosting::step() { printf '%s step=%s\n' "$HOSTING_MARKER" "$1"; }

# Human narration. Goes to stdout so it lands in the pod log the mesh streams back.
hosting::log() { printf '  %s\n' "$*"; }

# Fail loudly, naming the command. Never `exit 0` on a problem.
hosting::die() {
  printf '%s: ERROR: %s\n' "${HOSTING_CMD:-hosting}" "$*" >&2
  exit 1
}

# Require a non-empty environment variable, naming what to set when it is missing.
hosting::need_env() {
  local name="$1" why="${2:-}"
  local value="${!name:-}"
  [ -n "$value" ] || hosting::die "environment variable ${name} is empty or unset${why:+ — ${why}}. It is supplied by Hosting:Operator:Environment on the control instance."
}

# Require a non-empty flag value.
hosting::need_flag() {
  local name="$1" value="${2:-}"
  [ -n "$value" ] || hosting::die "missing required flag --${name}"
}

# Refuse anything that is not a plain identifier. Every name this operator receives ends up
# interpolated into an az/kubectl/helm command line, so the validation is a security boundary and
# not tidiness: the mesh composes the plan, but the mesh is driven by a node an admin can edit.
#
# 🚨 THESE VALIDATE IN PLACE AND PRINT NOTHING — deliberately. The obvious shape,
#     name="$(hosting::safe_name name "$name")"
# is BROKEN and silently so: a command substitution runs in a SUBSHELL, so the `exit 1` inside
# hosting::die ends the substitution and not the script. The caller carries on with an EMPTY value
# and every guard here becomes a no-op. Caught by test/run-tests.sh, which is why the injection
# cases in it are worth their length.
hosting::safe_name() {
  local what="$1" value="${2:-}"
  [[ "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] \
    || hosting::die "${what} '${value}' is not a plain name (letters, digits, dot, dash, underscore) — refusing to interpolate it into a command"
}

# A hostname, for DNS/TLS/probe steps. Validates in place; see the note above.
hosting::safe_host() {
  local what="$1" value="${2:-}"
  [[ "$value" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] \
    || hosting::die "${what} '${value}' is not a hostname — refusing"
}

# Is this a dry run? The mesh sets HOSTING_DRY_RUN=true for a rehearsal.
hosting::dry() { [ "${HOSTING_DRY_RUN:-false}" = "true" ]; }

# Run a command, or narrate it when rehearsing. Use for every MUTATION.
hosting::do() {
  # 🚨 The narration goes to STDERR, never stdout. `hosting::do` wraps MUTATIONS, and a mutation can
  # sit in a pipe whose consumer reads the command's output as data — hosting-deploy's
  #   hosting::do kubectl create namespace … --dry-run=client -o yaml | kubectl apply -f -
  # is one. Measured 2026-09-08 on memex (Deployments/memex-reconcile-20260908-ci8118): the
  # `  + kubectl create namespace …` line went down the pipe as line 1 of the manifest, `apiVersion:
  # v1` became line 2, and kubectl answered "yaml: line 2: mapping values are not allowed in this
  # context" — the Reconcile stopped at step 1/3 before helm ran, and every Provision would have
  # stopped at the same line. The job log (what the mesh's OperatorOutput parser reads) carries both
  # streams, so nothing an operator reads changes; only the data channel is clean.
  if hosting::dry; then
    printf '  DRY-RUN would run: %s\n' "$*" >&2
    return 0
  fi
  printf '  + %s\n' "$*" >&2
  "$@"
}

# Capture a command's stdout (queries, never mutations — a dry run still needs to read).
hosting::read() { "$@"; }

# ── DENIED, ABSENT and PRESENT are THREE answers, never two (MeshWeaver#4722) ───────────────────
#
# Run a READ and classify what came back:
#   0 PRESENT — it succeeded (and, with `output`, printed something)
#   1 ABSENT  — it failed for a reason that is NOT a refusal, or succeeded printing nothing
#   2 REFUSED — this identity was not permitted to look, so NOTHING is known either way
# Stdout lands in HOSTING_PROBE_OUT, the first line of stderr in HOSTING_PROBE_ERR.
#
# 🚨 WHY THIS IS A SHARED PRIMITIVE. The obvious shape is
#     value="$(kubectl get thing 2>/dev/null)"; [ -n "$value" ] || hosting::die "there is no thing"
# and `2>/dev/null` throws away the one fact that decides which sentence is true. A Forbidden —
# this operator's ClusterRole lacking the grant — comes out as the thing being ABSENT: the operator
# announcing that a platform layer, an Ingress or a ConfigMap does not exist when it was merely not
# permitted to LOOK, and sending the reader off to re-create something that is already there.
#
# It is the denied-vs-deleted confusion that closed MeshWeaver#1391 on a `Not found` and had the
# identical defect re-filed unchanged as #3883 four weeks later; here it is in an operator's own
# diagnostics, the one output a reader is supposed to trust.
#
# This lived as a local helper inside hosting-db-release when #4436 fixed the three probes #4722
# measured — which is exactly why the fix did not sweep: two more reads in that same file and two
# in other commands went on collapsing the two answers. A discrimination that only one script can
# reach is a discrimination the next script will not make.
#
# 🚨 Callers MUST branch on all three. `hosting::probe … || hosting::die "…absent…"` is the defect
# wearing the fix's clothes: it turns REFUSED back into ABSENT. Branch 2 first, and say that
# nothing was ruled out. test/check-stderr-discarded.sh is the static half.
HOSTING_PROBE_OUT="" HOSTING_PROBE_ERR=""
hosting::probe() {
  local need="$1"; shift
  local errfile rc
  errfile="$(mktemp)"
  HOSTING_PROBE_OUT="$("$@" 2>"$errfile")"; rc=$?
  HOSTING_PROBE_ERR="$(head -1 "$errfile")"; rm -f "$errfile"
  if [ "$rc" -ne 0 ]; then
    case "$HOSTING_PROBE_ERR" in
      *Forbidden*|*forbidden*) return 2 ;;
      *)                       return 1 ;;
    esac
  fi
  # `kubectl get nodes -l workload=db` exits 0 and prints NOTHING when the selector matches no
  # node. That is an ABSENCE, not a failure, and only the caller knows which reads apply.
  if [ "$need" = output ] && [ -z "$HOSTING_PROBE_OUT" ]; then return 1; fi
  return 0
}

# The one sentence a REFUSED probe is allowed to produce. It says what is NOT known, names the file
# the grant lives in and the lane that carries it — never that anything is absent, because a probe
# that could not run ruled nothing out.
hosting::die_refused() {
  hosting::die "REFUSED, not absent: this operator's ClusterRole does not permit reading $* — so whether it exists is UNKNOWN, and nothing was ruled out. Grant it in MeshWeaver deploy/aks/manifests/hosting-operator/operator-rbac.yaml; it reaches the cluster through Systemorph/Memex's helm-release lane, never from this Job. Refused: ${HOSTING_PROBE_ERR}"
}

# ── the Postgres admin password is resolved by NAME, never carried as a value (Memex#132) ──────
#
#   hosting::pg_password <vault> <object>
#
# Sets and exports PGPASSWORD for the pg_dump / psql / pg_restore that follow, read from Key Vault
# object <object> in vault <vault> with the identity this operator already holds — the SAME
# `az keyvault secret show --query value` hosting-kv-ensure makes to compose an instance's
# connection string, so no new grant is involved and the read is one this identity has already
# been trusted with. The value lands in this process only: never printed, never on an argv (az
# writes it to STDOUT, which is captured), never reported.
#
# 🚨 WHY A NAME. The only environment a plan step has is `Hosting:Operator:Environment` (the Job) or
# the bundle's `environment` map (the aks-ops lane) — both rendered from the control record into a
# ConfigMap, or a workflow_dispatch input: PLAINTEXT by construction. Every database action
# therefore failed at step 1 (`PGPASSWORD is empty or unset … supplied by
# Hosting:Operator:Environment`) rather than put the server's admin password there — and that
# sentence named a channel that cannot carry a secret. The record already carries the object's NAME
# (`operator.environment.AZ_POSTGRES_PASSWORD_SECRET`) and the vault's name (`keyVault`); the plan
# hands both to the script as `--vault` / `--password-secret`, exactly as it does to
# hosting-kv-ensure, and this resolves them on either executor.
#
# Both flags, or neither: with neither, a PGPASSWORD already in the environment is honoured (the
# by-hand shape — an operator at a shell with the password exported), and its absence is refused
# naming the flags, not the ConfigMap. The two names are validated here as plain identifiers
# because they are interpolated into an az command line — the same boundary hosting::safe_name
# guards everywhere else. ABSENT and REFUSED are two different sentences (MeshWeaver#4722): a
# vault that refused this identity has ruled nothing out about the object. An EMPTY value is refused
# as loudly as an unreadable one: psql with an empty PGPASSWORD prompts, and a Job that prompts
# hangs to its deadline saying nothing that names this step. A dry run must not call this — it reads
# a secret, and a rehearsal reads none (hosting-kv-ensure's rule).
hosting::pg_password() {
  local vault="${1:-}" object="${2:-}" rc
  if [ -z "$vault" ] && [ -z "$object" ]; then
    [ -n "${PGPASSWORD:-}" ] && return 0
    hosting::die "no Postgres admin password: pass --vault <Key Vault name> --password-secret <vault object name> — the plan supplies both from the control record (keyVault, and operator.environment.AZ_POSTGRES_PASSWORD_SECRET, e.g. memex-postgres-password), and this script reads the value from the vault as the identity it already runs as. The operator's environment is a ConfigMap and cannot carry the value (Memex#132); PGPASSWORD in the environment is honoured only when set by hand."
  fi
  [ -n "$vault" ] && [ -n "$object" ] \
    || hosting::die "--vault and --password-secret go together: the vault to read from and the object holding the server's admin password (got --vault '${vault}' --password-secret '${object}')"
  hosting::safe_name vault "$vault"
  hosting::safe_name password-secret "$object"
  hosting::probe output az keyvault secret show --vault-name "$vault" --name "$object" --query value -o tsv; rc=$?
  case "$rc" in
    2) hosting::die "REFUSED, not absent: the identity this step runs as may not read Key Vault object ${object} in vault ${vault}, so whether it exists is UNKNOWN. Grant it secret GET on that vault (hosting-kv-ensure reads the same object with the same identity when it composes a connection string, so a Provision that did that already holds the grant). Refused: ${HOSTING_PROBE_ERR}" ;;
    1) hosting::die "could not read Key Vault object ${object} from vault ${vault} — absent, or holding an EMPTY value. It must hold the admin password of the flexible server (the record's operator.environment.AZ_POSTGRES_PASSWORD_SECRET names it; hosting-kv-ensure composes connection strings from the same object). Nothing has been dumped, restored or destroyed. az said: ${HOSTING_PROBE_ERR:-nothing}" ;;
  esac
  PGPASSWORD="$HOSTING_PROBE_OUT"; HOSTING_PROBE_OUT=""
  export PGPASSWORD
  hosting::log "Postgres admin password read from vault ${vault} object ${object} (value never shown)"
}

# ── the plugin registry's key-lifecycle surface (MeshWeaver#2802) ───────────────────────────────

# A registry BASE URL: https, a hostname, an optional port — no path, no query, nothing else. It is
# interpolated into a curl command line, so it is validated like every other name. In place.
hosting::safe_url() {
  local what="$1" value="${2:-}"
  [[ "$value" =~ ^https://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$ ]] \
    || hosting::die "${what} '${value}' is not an https base URL (https://host[:port], nothing after it) — refusing"
}

# The decoded value of one key of a Secret, on STDOUT — for CAPTURE into a variable, never for
# printing. An absent Secret, an absent key and an empty value all print nothing and return 1;
# 🚨 a READ THIS IDENTITY WAS REFUSED returns 2, because "there is no such key" and "I was not
# allowed to look" are different sentences and only the caller can say which one it owes its
# reader (MeshWeaver#4722). A caller that only tests success is unaffected — both are non-zero.
# 🚨 Callers capture with $(...) and CHECK THE STATUS THEMSELVES: a hosting::die inside a command
# substitution ends the substitution, not the script (see hosting::safe_name).
hosting::secret_value() {
  local namespace="$1" secret="$2" key="$3" value rc
  hosting::probe any kubectl -n "$namespace" get secret "$secret" -o json; rc=$?
  [ "$rc" -eq 0 ] || return "$rc"
  value="$(printf '%s' "$HOSTING_PROBE_OUT" | jq -r --arg k "$key" '.data[$k] // empty' 2>/dev/null | base64 -d 2>/dev/null)" || return 1
  [ -n "$value" ] || return 1
  printf '%s' "$value"
}

# The containers of <deployment> in <namespace> that set <key> INLINE (an `env:` entry), joined by
# ", " on STDOUT — empty when none does. Names only; a value is never read out. Returns 1 when the
# Deployment cannot be read. An inline entry outranks every envFrom, so where one exists the pods
# present ITS value, and no Secret an operator step reads says which key that is.
# 🚨 Returns 2 when the read was REFUSED, 1 when the Deployment could not be read for any other
# reason — see hosting::secret_value above (MeshWeaver#4722).
hosting::inline_setters() {
  local namespace="$1" deployment="$2" key="$3" rc
  hosting::probe any kubectl -n "$namespace" get deployment "$deployment" -o json; rc=$?
  [ "$rc" -eq 0 ] || return "$rc"
  printf '%s' "$HOSTING_PROBE_OUT" | jq -r --arg k "$key" '[.spec.template.spec.containers[] | select(any(.env[]?; .name == $k)) | .name] | join(", ")'
}

# SHA-256 hex of STDIN — how two keys are compared without either being shown.
hosting::sha256() { sha256sum | cut -c1-64; }

# One call to the registry, AUTHENTICATED BY THE KEY IN THE NAMED VARIABLE:
#   hosting::registry_call <key-variable-name> <GET|POST> <url> [json-body]
# Sets REGISTRY_STATUS (the HTTP code; "000" when nothing answered) and REGISTRY_BODY.
#
# 🚨 The key travels by variable NAME and reaches curl on STDIN as a config line (`-K -`), so it is
# never an ARGUMENT — not this function's, not curl's, never visible in `ps` while the call runs.
# printf is a shell builtin: it forks no process that could carry the key in its argv either. The
# body is only ever a HASH. Must not be called in a command substitution (it sets globals).
# shellcheck disable=SC2034  # REGISTRY_STATUS/REGISTRY_BODY are read by the scripts sourcing this file
REGISTRY_STATUS="" REGISTRY_BODY=""
hosting::registry_call() {
  local keyvar="$1" method="$2" url="$3" body="${4:-}" out
  out="$(mktemp)"
  local args=(-sS -K - -X "$method" -o "$out" -w '%{http_code}' --max-time 20)
  [ -z "$body" ] || args+=(-H 'Content-Type: application/json' --data "$body")
  REGISTRY_STATUS="$(printf 'header = "Authorization: Bearer %s"\n' "${!keyvar}" | curl "${args[@]}" "$url" 2>/dev/null)" || true
  REGISTRY_STATUS="${REGISTRY_STATUS:-000}"
  REGISTRY_BODY="$(cat "$out" 2>/dev/null || true)"
  rm -f "$out"
}

# One field of the last registry answer (REGISTRY_BODY), or nothing.
hosting::registry_field() {
  printf '%s' "$REGISTRY_BODY" | jq -r --arg f "$1" 'if type == "object" then (.[$f] // empty | tostring) else empty end' 2>/dev/null
}
