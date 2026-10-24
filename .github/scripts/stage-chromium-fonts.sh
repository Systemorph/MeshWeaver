#!/usr/bin/env bash
# The font set Chromium prints CI's PDFs with. It is staged once, off the shards' critical path,
# and a shard only unpacks it.
#
#   stage-chromium-fonts.sh key     <list>                 # the cache key, identical in both jobs
#   stage-chromium-fonts.sh stage   <list> <out.tar.zst>   # browser-fonts job, on a cache miss only
#   stage-chromium-fonts.sh install <tarball>              # every test shard
#
# ── Why ──────────────────────────────────────────────────────────────────────
# Every test shard used to run `playwright-core install --with-deps` for its apt half, and the
# only thing it added on ubuntu-24.04 was nine FONT packages. Every Chromium library is already in
# the image. When azure.archive.ubuntu.com stalled, three bounded attempts spent the step's whole
# 480 s, and the step then carried on without the fonts anyway. On 2026-10-23 that cut four
# shards at the 20-minute job cap with their tests still progressing: PR runs 40011143666 and
# 40011313475 and main run 40027189653, all attempt 1. So a shard paid eight minutes for nothing,
# on a mirror it did not need to reach at all.
#
# Fonts are DATA, so they need no dpkg install. `stage` downloads the listed .debs, extracts their
# files with `dpkg-deb -x` into a private root and packs `usr/share/fonts` and `etc/fonts` from
# it. `install` unpacks that into / and refreshes fontconfig. No apt runs in a shard, so there is
# no apt lock, no orphaned apt-get to reap and no mirror round trip.
#
# 🚨 `stage` also uses no system apt state. Its `apt-get update` and `apt-get download` run
# against a private Dir::State/Dir::Cache with locking off, as the runner user. The runner
# image's own background apt therefore cannot block it, and a killed attempt leaves nothing
# behind that holds a lock.
#
# 🚨 `stage` fails LOUDLY. A drifted list, a stall or a missing package all fail it, and the
# browser-fonts job is red with the reason. Nothing is cached, so the next run tries again. A
# shard whose cache restore found nothing says so in a ::warning:: and prints with the runner's
# own fonts. The smoke print still decides whether the browser works, exactly as when
# `--with-deps` failed before.
set -euo pipefail

mode="${1:-}"

# Seconds each network phase of `stage` may take. The job's own `timeout-minutes` is the
# end-to-end bound; these turn a stall into a named failure inside it.
STAGE_NET_TIMEOUT="${STAGE_NET_TIMEOUT:-240}"

listed_packages() { grep -vE '^[[:space:]]*(#|$)' "$1" | sed -e 's/[[:space:]]//g' | grep -v '^$' || :; }

# The font packages the pinned Playwright asks for on THIS runner's release, from its own
# dry run. Anything that is neither in the list nor already installed is DRIFT.
check_drift() {
  local list="$1" asked missing="" p
  : "${PLAYWRIGHT_VERSION:?PLAYWRIGHT_VERSION must be set}"
  asked=$(timeout --signal=TERM --kill-after=15 "$STAGE_NET_TIMEOUT" \
            npx --yes "playwright-core@${PLAYWRIGHT_VERSION}" install-deps --dry-run chromium 2>/dev/null \
          | tr -s '[:space:]"' '\n' | grep -E '^x?fonts-[A-Za-z0-9.+-]+$' | sort -u || :)
  if [ -z "$asked" ]; then
    echo "::error::playwright-core@${PLAYWRIGHT_VERSION} install-deps --dry-run named no font package — the drift check measured nothing, so the list in $list cannot be trusted. Refusing to stage."
    return 1
  fi
  for p in $asked; do
    if listed_packages "$list" | grep -qxF "$p"; then continue; fi
    if dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q 'install ok installed'; then continue; fi
    missing="$missing $p"
  done
  if [ -n "$missing" ]; then
    echo "::error::playwright-core@${PLAYWRIGHT_VERSION} installs font package(s)${missing} on this release, which $list does not stage and the runner image does not have. Add them to $list."
    return 1
  fi
  echo "drift check: every font package Playwright asks for is staged or already installed ($(echo "$asked" | wc -w | tr -d ' ') asked)"
}

stage() {
  local list="$1" out="$2" work pkgs n
  work=$(mktemp -d)
  pkgs=$(listed_packages "$list")
  [ -n "$pkgs" ] || { echo "::error::$list lists no packages"; return 1; }
  check_drift "$list"

  # A private apt state: own lists, own archive cache, no locks, no root.
  mkdir -p "$work/state/lists/partial" "$work/cache/archives/partial" "$work/debs" "$work/root"
  local -a apt_opts=(-o "Dir::State=$work/state" -o "Dir::State::Lists=$work/state/lists"
                     -o "Dir::State::status=/var/lib/dpkg/status" -o "Dir::Cache=$work/cache"
                     -o "Dir::Cache::Archives=$work/cache/archives"
                     -o "Debug::NoLocking=1"
                     -o "Acquire::Retries=2" -o "Acquire::http::Timeout=30")
  timeout --signal=TERM --kill-after=15 "$STAGE_NET_TIMEOUT" apt-get "${apt_opts[@]}" update -qq
  # shellcheck disable=SC2086
  ( cd "$work/debs" && timeout --signal=TERM --kill-after=15 "$STAGE_NET_TIMEOUT" \
      apt-get "${apt_opts[@]}" download $pkgs )

  n=0
  for deb in "$work"/debs/*.deb; do
    [ -e "$deb" ] || continue
    dpkg-deb -x "$deb" "$work/root"
    n=$((n + 1))
  done
  if [ "$n" -ne "$(echo "$pkgs" | wc -w | tr -d ' ')" ]; then
    echo "::error::downloaded $n .deb(s) for $(echo "$pkgs" | wc -w | tr -d ' ') listed package(s) — refusing to cache a partial font set"
    return 1
  fi
  [ -d "$work/root/usr/share/fonts" ] || { echo "::error::the staged packages carry no usr/share/fonts"; return 1; }

  local -a trees=(usr/share/fonts)
  [ -d "$work/root/etc/fonts" ] && trees+=(etc/fonts)
  tar -C "$work/root" -I 'zstd -T0 -19' -cf "$out" "${trees[@]}"
  echo "staged $n package(s), $(find "$work/root/usr/share/fonts" -type f | wc -l | tr -d ' ') font file(s), $(du -h "$out" | cut -f1) → $out"
  rm -rf "$work"
}

install_fonts() {
  local tarball="$1" before after
  before=$(fc-list : file 2>/dev/null | wc -l | tr -d ' ')
  sudo tar -I zstd -xf "$tarball" -C /
  timeout --signal=TERM --kill-after=5 "${FONT_CACHE_TIMEOUT:-60}" sudo fc-cache -f >/dev/null
  after=$(fc-list : file 2>/dev/null | wc -l | tr -d ' ')
  if [ "$after" -le "$before" ]; then
    echo "::warning::the staged font set unpacked but fontconfig sees no new face ($before → $after). PDFs print with the runner's own fonts."
    return 0
  fi
  echo "font set installed: fontconfig faces $before → $after"
}

# The cache key. ONE definition, so the job that stages and the shards that restore cannot
# disagree. It changes with the runner's release and architecture (the .debs are per release),
# with the Playwright pin (whose font set the list must match) and with this script and the list.
cache_key() {
  local list="$1" codename digest
  : "${PLAYWRIGHT_VERSION:?PLAYWRIGHT_VERSION must be set}"
  # shellcheck disable=SC1091  # the runner's own release file, read at run time
  codename=$(. /etc/os-release && echo "${VERSION_CODENAME:-unknown}")
  digest=$(cat "$list" "${BASH_SOURCE[0]}" | sha256sum | cut -c1-16)
  echo "chromium-fonts-v1-${codename}-$(uname -m)-pw${PLAYWRIGHT_VERSION}-${digest}"
}

case "$mode" in
  key)     cache_key "${2:?list}" ;;
  stage)   stage "${2:?list}" "${3:?out}" ;;
  install) install_fonts "${2:?tarball}" ;;
  *) echo "usage: $0 key <list> | stage <list> <out.tar.zst> | install <tarball>" >&2; exit 2 ;;
esac
