#!/usr/bin/env bash
# Wait for a competing apt on this runner to FINISH — bounded.
#
# The runner image runs its own background apt (unattended-upgrades / the daily timer). While it
# holds the lock, `playwright install --with-deps` shells out to apt-get and exits 100 with
#   E: Could not get lock /var/lib/apt/lists/lock. It is held by process N (apt-get)
# which is CONTENTION, not a broken download, so retrying immediately just re-races the same holder.
#
# 🚨 TWO THINGS THIS DELIBERATELY DOES NOT DO, both measured on real runs (#1855, #1858):
#   • It does not `flock` the lock file. flock(1) takes a BSD lock; apt and dpkg take POSIX fcntl
#     locks on those same files, and on Linux those are independent lock spaces — so the flock is
#     granted instantly against a lock apt is actively holding. In run 32166749364 the three
#     install attempts were separated by the retry backoff alone (10s, 20s), never by the 60s the
#     wait claimed, while one apt-get held the lock throughout.
#   • It does not wait for the lock to be momentarily FREE. A momentary check reserves nothing:
#     the background apt re-takes it before playwright's own apt-get gets there, which is why a
#     wait on the lock files still failed with the same holder immediately after reporting it free.
# What settles it is the holding PROCESS exiting. That is what this waits for.
#
# Usage: wait-for-apt.sh [budget-seconds]
set -euo pipefail

budget="${1:-180}"
interval=3

if ! command -v pgrep >/dev/null 2>&1; then
  # Never silent: a wait that cannot run must say so, or its absence is indistinguishable from
  # "apt was free" — which is how the flock version hid for as long as it did.
  echo "::warning::pgrep is not available — cannot wait for a competing apt; continuing (the caller's step is bounded)" >&2
  exit 0
fi

busy() {
  pgrep -x apt-get >/dev/null 2>&1 ||
  pgrep -x apt >/dev/null 2>&1 ||
  pgrep -x dpkg >/dev/null 2>&1 ||
  pgrep -f unattended-upgr >/dev/null 2>&1
}

waited=0
while [ "$waited" -lt "$budget" ] && busy; do
  [ "$waited" -eq 0 ] && echo "a competing apt is running — waiting up to ${budget}s for it to finish" >&2
  sleep "$interval"
  waited=$((waited + interval))
done

if [ "$waited" -gt 0 ]; then
  if busy; then
    # Reported, not fatal: apt may still finish before the caller reaches it, and every caller
    # bounds its own attempt. But a run that failed AFTER a full wait must be distinguishable from
    # one that never waited — the distinction the flock version destroyed.
    echo "::warning::a competing apt is STILL running after ${budget}s; continuing anyway" >&2
  else
    echo "waited ${waited}s for a competing apt to finish" >&2
  fi
fi
