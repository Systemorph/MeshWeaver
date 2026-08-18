#!/usr/bin/env bash
# Wait until no apt/dpkg process is running, so a following `apt-get` (usually via
# `playwright install --with-deps`) can take the lock instead of losing a race to the runner
# image's own background apt (unattended-upgrades / the daily timer).
#
# 🚨 WAIT FOR THE HOLDER TO FINISH — do not "check the lock". The shape this replaced was
#   sudo flock -w N /var/lib/apt/lists/lock true
# which ACQUIRES the lock and releases it in the same breath, reserving nothing: the holder
# re-takes it before playwright's apt-get gets there. Measured — after that wait reported the
# lock free, the very next line still failed with
#   E: Could not get lock /var/lib/apt/lists/lock. It is held by process 3014 (apt-get)
# apt also locks with fcntl, which does not interoperate with flock, so that shape could not
# have been reliable even without the release race. See #1843 / #1858.
#
# Bounded by the first argument (default 240s): it can wait, it cannot hang. Exits 0 either way —
# a timed-out wait is not a reason to skip the install, which is bounded on its own.
set -euo pipefail
budget="${1:-240}"
waited=0
while [ "$waited" -lt "$budget" ]; do
  pgrep -x apt-get >/dev/null 2>&1 || pgrep -f unattended-upgr >/dev/null 2>&1 || break
  sleep 3
  waited=$((waited + 3))
done
[ "$waited" -gt 0 ] && echo "waited ${waited}s for a competing apt to finish" >&2
exit 0
