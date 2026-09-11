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

# ── the plugin registry's key-lifecycle surface (MeshWeaver#2802) ───────────────────────────────

# A registry BASE URL: https, a hostname, an optional port — no path, no query, nothing else. It is
# interpolated into a curl command line, so it is validated like every other name. In place.
hosting::safe_url() {
  local what="$1" value="${2:-}"
  [[ "$value" =~ ^https://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$ ]] \
    || hosting::die "${what} '${value}' is not an https base URL (https://host[:port], nothing after it) — refusing"
}

# The decoded value of one key of a Secret, on STDOUT — for CAPTURE into a variable, never for
# printing. An absent Secret, an absent key and an empty value all print nothing and return 1.
# 🚨 Callers capture with $(...) and CHECK THE STATUS THEMSELVES: a hosting::die inside a command
# substitution ends the substitution, not the script (see hosting::safe_name).
hosting::secret_value() {
  local namespace="$1" secret="$2" key="$3" json value
  json="$(kubectl -n "$namespace" get secret "$secret" -o json 2>/dev/null)" || return 1
  value="$(printf '%s' "$json" | jq -r --arg k "$key" '.data[$k] // empty' 2>/dev/null | base64 -d 2>/dev/null)" || return 1
  [ -n "$value" ] || return 1
  printf '%s' "$value"
}

# The containers of <deployment> in <namespace> that set <key> INLINE (an `env:` entry), joined by
# ", " on STDOUT — empty when none does. Names only; a value is never read out. Returns 1 when the
# Deployment cannot be read. An inline entry outranks every envFrom, so where one exists the pods
# present ITS value, and no Secret an operator step reads says which key that is.
hosting::inline_setters() {
  local namespace="$1" deployment="$2" key="$3" json
  json="$(kubectl -n "$namespace" get deployment "$deployment" -o json 2>/dev/null)" || return 1
  printf '%s' "$json" | jq -r --arg k "$key" '[.spec.template.spec.containers[] | select(any(.env[]?; .name == $k)) | .name] | join(", ")'
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
