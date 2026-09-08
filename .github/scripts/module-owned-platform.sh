#!/usr/bin/env bash
# The MODULE-OWNED MeshWeaver.* assemblies of a node repo: every MeshWeaver.* project DIRECTORY in
# its src/ that the PLATFORM does not ship. A module bundle may carry the former (they exist nowhere
# in the host) and must never carry the latter (a same-identity duplicate beside the host's own
# copy). Printed semicolon-separated, the shape `meshweaver-plugin-build module-pack
# --own-platform` takes; the bundle inspection in node-repo-module-pack.yml calls this same script
# so the two cannot disagree.
#
#   module-owned-platform.sh <node-repo>/src [<platform-app-dir>]
#
# 🚨 WHICH WITNESS ANSWERS, AND WHY IT MATTERS (MeshWeaver#3732).
#
#   * WITH <platform-app-dir> — a portal image's extracted /app — the answer is MEASURED off what
#     that host ACTUALLY ships, three ways, because the image ships assemblies three ways:
#       1. <app>/<Name>.dll                        the application closure
#       2. <app>/meshweaver-surface.manifest       the host's own record of its compile references
#       3. <app>/modules/<Name>/<Name>.dll         the seeded-module lane (MeshModulesPublish's
#                                                  CLOSURE lane, which MeshBuilder.ResolveModulePath
#                                                  probes FIRST)
#     🚨 A `MeshModuleClosure` row touches NEITHER (1) NOR (2) — the portal hosts' csproj comments
#     say so in as many words — so a witness reading only /app answers "not shipped" for every
#     seeded module. That is how MeshWeaver.Markdown.Collaboration came to ride 14 of the 37
#     MeshWeaver.Plugins bundles while the image seeds it too (Plugins#1515).
#
#   * WITHOUT one, the fallback is the repo's DECLARED list, src/platform-shipped.txt. It is a
#     declaration, not a measurement, and it has been wrong in both directions inside one week:
#     #3335 found five names in /app that it did not list (a duplicate in every bundle referencing
#     one) and corrected it BY HAND; the same file previously listed MeshWeaver.Maps after the
#     image stopped shipping it, so that name reached a mesh from nowhere at all. The fallback
#     exists only for the lane that pins no platform image (core's own CD builds the platform from
#     source, so there is no /app to read); it SAYS which witness answered, on stderr, every time.
#
# When both are available the IMAGE decides and the declared list is compared to it, with any
# disagreement printed on stderr as a `DRIFT` line naming the exact entry to add or drop. The list
# then decides nothing and can be deleted; leaving it stale costs a log line, never a wrong bundle.
#
# An absent platform-shipped.txt means "nothing is image-shipped" — the state of every node repo
# before the storage carve-out — and is deliberately not an error: the list DECLARES exclusions.
# An EMPTY result (a repo whose every MeshWeaver.* project is image-shipped, or one with none) is
# valid too, and prints an empty line rather than failing.
set -euo pipefail
src="${1:?usage: module-owned-platform.sh <node-repo>/src [<platform-app-dir>]}"
app="${2:-}"

declared=""
if [ -f "$src/platform-shipped.txt" ]; then
  declared="$(grep -v '^[[:space:]]*#' "$src/platform-shipped.txt" | sed 's/[[:space:]]*$//' | grep -v '^$' || true)"
fi

shipped=""
if [ -n "$app" ]; then
  [ -d "$app" ] || { echo "::error::module-owned-platform.sh: platform app directory '$app' does not exist. A witness that cannot read the host would fall back to a declared list while looking like a measurement — refusing instead (MeshWeaver#3732)." >&2; exit 2; }
  # 1. the application closure. The name test mirrors PlatformShippedAssemblies.IsPlatformAssemblyName
  #    exactly — `MeshWeaver` or `MeshWeaver.<something>`, never `MeshWeaverish`.
  root_dlls="$(find "$app" -maxdepth 1 -type f \( -name 'MeshWeaver.*.dll' -o -name 'MeshWeaver.dll' \) \
    | sed 's#.*/##; s#\.dll$##' | sort -u)"
  # 2. the host's own surface manifest (<name>=<sha256> per line)
  manifest_names=""
  if [ -f "$app/meshweaver-surface.manifest" ]; then
    manifest_names="$(sed -n 's/^\(MeshWeaver\(\.[^=]*\)\{0,1\}\)=.*$/\1/p' "$app/meshweaver-surface.manifest" | sort -u)"
  fi
  # 3. the seeded-module lane — ONLY modules/<Name>/<Name>.dll, the file ResolveModulePath probes;
  #    the rest of a seeded folder is that module's own private closure, on nobody else's path.
  seeded=""
  if [ -d "$app/modules" ]; then
    while IFS= read -r dir; do
      name="$(basename "$dir")"
      case "$name" in MeshWeaver.?*|MeshWeaver) ;; *) continue ;; esac
      [ -f "$dir/$name.dll" ] || continue
      seeded="$seeded$name
"
    done < <(find "$app/modules" -mindepth 1 -maxdepth 1 -type d | sort)
  fi
  # 🚨 An empty reading is a WRONG DIRECTORY, never an answer. A portal /app carries 200+
  # MeshWeaver.* assemblies and the tester image 88; "this host ships none" would silently turn
  # every exclusion below into a ride while logging like a clean measurement.
  if [ -z "$root_dlls" ] && [ -z "$manifest_names" ]; then
    echo "::error::module-owned-platform.sh: '$app' carries no meshweaver-surface.manifest and no MeshWeaver.*.dll at its root, so it is not a platform application directory. Pass a portal image's extracted /app." >&2
    exit 2
  fi
  shipped="$(printf '%s\n%s\n%s\n' "$root_dlls" "$manifest_names" "$seeded" | grep -v '^$' | sort -u)"
  echo "module-owned-platform: MEASURED against $app — $(printf '%s\n' "$shipped" | grep -c '[^[:space:]]' || true) MeshWeaver.* assembl(y|ies) shipped by that host" >&2
  # The declared list decides nothing now; say where it disagrees so it can be corrected or deleted.
  # 🚨 PLAIN stderr, never `::warning::`. This script runs THREE times per matrix entry — 111 times
  # on MeshWeaver.Plugins' 37 — and a drift line is diagnostic about a file that decides nothing, so
  # annotating it would bury the annotations that do decide something under two hundred that do not.
  # The refusals above stay `::error::`: those stop the pack.
  if [ -n "$declared" ]; then
    while IFS= read -r name; do
      [ -n "$name" ] || continue
      grep -qxF "$name" <<<"$shipped" \
        || echo "module-owned-platform: DRIFT — src/platform-shipped.txt names '$name' as image-shipped, but $app does not ship it. The line is stale; before this measurement existed it kept that assembly OUT of every bundle that needs it." >&2
    done <<<"$declared"
    while IFS= read -r name; do
      [ -n "$name" ] || continue
      [ -d "$src/$name" ] || continue
      grep -qxF "$name" <<<"$declared" \
        || echo "module-owned-platform: DRIFT — $app ships '$name' and src/platform-shipped.txt does not name it. Before this measurement existed, every bundle referencing it carried a second build of it." >&2
    done <<<"$shipped"
  fi
else
  shipped="$declared"
  echo "module-owned-platform: no platform app directory given — the split rests on the DECLARED src/platform-shipped.txt ($(printf '%s\n' "$declared" | grep -c '[^[:space:]]' || true) name(s)), which is not a measurement. Pass the pinned image's extracted /app to measure it (MeshWeaver#3732)." >&2
fi

own=()
while IFS= read -r dir; do
  name="$(basename "$dir")"
  if ! grep -qxF "$name" <<<"$shipped"; then own+=("$name"); fi
done < <(find "$src" -mindepth 1 -maxdepth 1 -type d -name 'MeshWeaver.*' | sort)
( IFS=';'; printf '%s\n' "${own[*]-}" )
