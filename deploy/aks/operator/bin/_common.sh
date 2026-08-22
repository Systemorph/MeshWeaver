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
  if hosting::dry; then
    printf '  DRY-RUN would run: %s\n' "$*"
    return 0
  fi
  printf '  + %s\n' "$*"
  "$@"
}

# Capture a command's stdout (queries, never mutations — a dry run still needs to read).
hosting::read() { "$@"; }
