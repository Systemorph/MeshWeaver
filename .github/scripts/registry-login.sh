#!/usr/bin/env bash
#
# registry-login.sh <registry host> <username>      (the password in $REGISTRY_PASSWORD)
#
# `docker login` to the fleet's own registry (cr.meshweaver.cloud — Doc/Architecture/
# ContainerRegistryInMemex) as the publisher, the ONE static docker_auth account that may push.
# The credential lands in ~/.docker/config.json, which is the store BOTH clients this lane uses
# read: `docker buildx imagetools` (the image mirror) and ORAS (the bundle publisher). One login,
# both registries' tools.
#
# 🚨 The password comes from the ENVIRONMENT, never argv: a process's arguments are readable by
# every other process on the runner and land in `ps` output; an environment variable is not.
#
# 🚨 Bounded TRANSPORT retry, same shape as acr-login.sh (5 attempts, 5s→40s backoff, loud
# between attempts, red after the last) — and, like the publish retry, it DISCRIMINATES BY FAILURE
# CLASS. A refused credential (`unauthorized`, `401`, docker_auth's `WrongPass`) is deterministic:
# the same password fails the same way on every attempt, so it fails IMMEDIATELY naming the secret
# to rotate, rather than burning the backoff window to report the same refusal five times.
#
# 🚨 An EMPTY password is asserted RED here, naming the secret, even though every lane's preflight
# already asserts it: this script is what a caller pinned to an older lane reaches, and "docker
# login … --password-stdin" with an empty stdin answers a misleading `unauthorized`.
set -uo pipefail

HOST="${1:-}"
USER_NAME="${2:-}"
[ -n "$HOST" ] || { echo "::error::registry-login: usage: registry-login.sh <registry host> <username> (password in \$REGISTRY_PASSWORD)"; exit 2; }
[ -n "$USER_NAME" ] || { echo "::error::registry-login: usage: registry-login.sh <registry host> <username> (password in \$REGISTRY_PASSWORD)"; exit 2; }
if [ -z "${REGISTRY_PASSWORD:-}" ]; then
  echo "::error::registry-login: \$REGISTRY_PASSWORD is EMPTY — the fleet registry's publisher password (secrets.MW_REGISTRY_PUBLISHER_PASSWORD on the calling repository; Key Vault 'Systemorph' → memexcloud-Registry-PublisherPassword) did not reach this step. Provision it in BOTH secret stores (Actions AND Dependabot) and check the lane passes it through."
  exit 1
fi
command -v docker > /dev/null 2>&1 || { echo "::error::registry-login: docker is not on PATH — the credential store this login writes is ~/.docker/config.json"; exit 1; }

attempts=5
log="${RUNNER_TEMP:-/tmp}/registry-login-$$.log"
for attempt in $(seq 1 "$attempts"); do
  if printf '%s' "$REGISTRY_PASSWORD" | docker login "$HOST" --username "$USER_NAME" --password-stdin > "$log" 2>&1; then
    [ "$attempt" -gt 1 ] && echo "registry-login: reached $HOST on attempt $attempt"
    echo "registry-login: logged in to $HOST as $USER_NAME"
    rm -f "$log"
    exit 0
  fi
  if grep -qiE 'unauthorized|401|WrongPass|incorrect username or password|access denied' "$log"; then
    cat "$log" >&2
    echo "::error::registry-login: $HOST REFUSED the publisher credential for '$USER_NAME' — deterministic, not retried. Rotate secrets.MW_REGISTRY_PUBLISHER_PASSWORD from Key Vault 'Systemorph' → memexcloud-Registry-PublisherPassword (the bcrypt hash in the chart's registry.publisher.passwordBcrypt must be of that same password), in BOTH secret stores."
    rm -f "$log"
    exit 1
  fi
  if [ "$attempt" -lt "$attempts" ]; then
    delay=$((5 * 2 ** (attempt - 1)))
    echo "registry-login: attempt $attempt failed ($(tr '\n' ' ' < "$log")); retrying in ${delay}s" >&2
    sleep "$delay"
  fi
done
cat "$log" >&2
rm -f "$log"
echo "::error::registry-login: docker login $HOST failed after $attempts attempts — the registry was unreachable from this runner for the whole backoff window (~75s), not a blip. Nothing was published there; the ACR half of this publication is unaffected."
exit 1
