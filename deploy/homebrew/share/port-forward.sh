#!/bin/bash
# =============================================================================
# memex-local port-forward — 1:1 automation of LocalColimaMac.md §7
# =============================================================================
# Run by the launchd login agent (com.memex.local.plist) with RunAtLoad +
# KeepAlive so http(s)://memex.localhost (and legacy :8443) survives reboots with NO sudo.
# `memex-local up` installs this script to ~/.memex-local/port-forward.sh and
# wires the plist; `memex-local port-forward` can also run it directly.
#
# It does three things, in order (exactly as the doc's §7 snippet):
#   1. Start Colima if it isn't running (brings k3s + the portal back).
#   2. Wait until the ingress-nginx namespace/controller exists.
#   3. Forward host :8443 to the ingress controller's :443.
# =============================================================================
set -uo pipefail

# launchd gives login agents a minimal PATH; make the brew tools reachable.
export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:${PATH:-}"

HOST_PORT="${MEMEX_PORT:-8443}"

# 1. Start Colima if it isn't running.
colima status >/dev/null 2>&1 || colima start

# 2. Wait until the ingress-nginx namespace/controller is ready.
until kubectl get ns ingress-nginx >/dev/null 2>&1; do sleep 2; done

# 3. Forward the portless URLs AND the legacy port: one process carries all three
#    mappings, so http://memex.localhost and https://memex.localhost work directly
#    (macOS allows unprivileged binds below 1024 since Mojave — still NO sudo) and
#    https://memex.localhost:8443 keeps working for anything that learned it.
exec kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller \
    80:80 443:443 "${HOST_PORT}:443"
