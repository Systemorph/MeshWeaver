#!/bin/bash
set -euo pipefail
SRC="$1"
STAGE="$2"
rm -rf "$STAGE"
mkdir -p "$STAGE"
# The trees that carry runtime-compiled Source/*.cs and NodeType definitions — every one of them.
# `Type` is gated too: it holds a real NodeType (`Type/Article.json`, configuration
# `config => config.WithContentType<Article>().AddNodeTypeView()`) and its Code node
# (`Type/Article/Source/Article.cs`), which nothing else type-checks (#7344).
GATED="MathDemo PythonDemo Systemorph Type"
# Every OTHER top-level tree is NOT gated, and the reason is ENFORCED below rather than stated:
#   * User — the gate mesh serves 'User' from a static node provider (AddMeshNodes), and installing
#     over a static-provider-served path is refused by design (MeshWeaver#1209). It carries
#     `User/Source/Person.cs`, so it is the one named exception — known and listed here.
#   * every other tree (welcome, login, Admin, Agent, ApiToken, VUser, Doc, MeshWeaver, TestSpace,
#     Brand, Architecture, …) holds no compilable content. The check below FAILS the gate the
#     moment one of them grows a NodeType definition or a `.cs` file, so this sentence can no
#     longer go stale silently the way it did for `Type` (#7344, finding 2).
# Cornerstone is NOT here any more: it moved to MeshWeaver.Plugins as a Store package when
# MeshWeaver.Maps left the content surface — its RiskMap view binds MapControl, so the content
# had to travel with the module. Its suites moved with it and run there.
# ACME, Northwind, FutuRe and PensionFund are NOT here any more either: they are product demos, not
# framework samples, and moved to MeshWeaver.Plugins' test/samples/Graph with the suites that load
# them (Doc/Architecture/CarvingProjectsOutOfCore -> "The product demo samples").
#
# 🚨 WHAT THIS GATE DOES NOT COVER (#7344, finding 1): each tree is staged at its AUTHORED ROOT
# (`MathDemo/…`), never under the prefix a statically-imported partition serves it from
# (`MeshWeaver/samples/Graph/Data/MathDemo/…`). A defect that only exists under a mount prefix — the
# class of #4813, an absolute `shared=@Tree/…` source reference resolved verbatim — therefore cannot
# fail it. That class is pinned where it lives, in the resolver (`CodeIncludePatternTest`, which
# drives the mount-anchored spelling under both a root and a prefixed mount); see
# Doc/Architecture/MountRelativeSourceQueries → "What the samples gate does and does not prove".
UNGATED_WITH_SOURCE="User"
problems=()
for dir in "$SRC"/*/; do
  name="$(basename "$dir")"
  case " $GATED $UNGATED_WITH_SOURCE " in *" $name "*) continue;; esac
  while IFS= read -r f; do problems+=("$f"); done < <(
    { find "$dir" -name '*.cs' -type f; grep -rlE '"nodeType"[[:space:]]*:[[:space:]]*"NodeType"' "$dir" --include='*.json' || true; } | sort -u)
done
# A sibling `<Tree>.json` root can itself be a NodeType definition — check those too.
for f in "$SRC"/*.json; do
  name="$(basename "$f" .json)"
  case " $GATED $UNGATED_WITH_SOURCE " in *" $name "*) continue;; esac
  if grep -qE '"nodeType"[[:space:]]*:[[:space:]]*"NodeType"' "$f"; then problems+=("$f"); fi
done
if [ "${#problems[@]}" -gt 0 ]; then
  echo "::error::samples gate: ${#problems[@]} compilable file(s) sit in a tree the gate does not stage — nothing would type-check them. Add the tree to GATED in $0 (or, with a reason, to UNGATED_WITH_SOURCE):"
  printf '  %s\n' "${problems[@]}"
  exit 1
fi
for name in $GATED; do
  if [ ! -d "$SRC/$name" ]; then echo "::error::samples gate: expected tree '$name' is missing"; exit 1; fi
  mkdir -p "$STAGE/$name"
  cp -R "$SRC/$name/." "$STAGE/$name/"
  rm -f "$STAGE/$name/index.md"
  # Uniform synthesized Space root: the FileSystem layout keeps roots as SIBLING jsons carrying
  # persistence stamps (version/createdDate) that have no business in a fresh gate install.
  printf '%s' "{\"\$type\":\"MeshNode\",\"id\":\"$name\",\"path\":\"$name\",\"mainNode\":\"$name\",\"name\":\"$name\",\"nodeType\":\"Space\",\"state\":\"Active\"}" > "$STAGE/$name/index.json"
done
echo "staged: $(find "$STAGE" -name '*.cs' | wc -l | tr -d ' ') .cs files, $(ls "$STAGE" | wc -l | tr -d ' ') packages"
