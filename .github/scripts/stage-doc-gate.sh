#!/bin/bash
# stage-doc-gate.sh <src-data-dir> <stage-dir>
#
# Stages src/MeshWeaver.Documentation/Data as the `Doc/` package root mw-plugin-test imports —
# the shape BOTH consumers need, which is why it lives here rather than inline in one workflow:
#
#   * dotnet-test.yml's `doc-gate` — the PR verdict ("the Doc tree compiles, renders, tests"),
#   * main-cd.yml's `publish-bake` — the SHIPPED IMAGE re-running the same content to produce the
#     bundles the pods adopt.
#
# Those two must stage byte-identical trees: the bake publishes what the gate proved. A second,
# drifting copy of this staging is exactly how a bake could publish something the gate never judged.
#
# The tree has no index.json of its own (index.md is the partition's root PAGE, and committing a
# JSON root would ship a duplicate root node in the embedded partition), so the root is synthesized
# HERE and index.md is dropped from the STAGE only — shipped content is untouched. NodeFileMapper
# then maps Doc/DataMesh/SocialMedia/Post.json et al. to the exact namespaces the files themselves
# declare, so the gate compiles the tree under its canonical paths.
set -euo pipefail
SRC="${1:?usage: stage-doc-gate.sh <src-data-dir> <stage-dir>}"
STAGE="${2:?usage: stage-doc-gate.sh <src-data-dir> <stage-dir>}"
if [ ! -d "$SRC" ]; then echo "::error::doc gate: source tree '$SRC' is missing"; exit 1; fi
rm -rf "$STAGE"
mkdir -p "$STAGE/Doc"
cp -R "$SRC/." "$STAGE/Doc/"
rm -f "$STAGE/Doc/index.md"
printf '%s' '{"$type":"MeshNode","id":"Doc","path":"Doc","mainNode":"Doc","name":"MeshWeaver Documentation","nodeType":"Space","state":"Active"}' > "$STAGE/Doc/index.json"
echo "staged: $(find "$STAGE" -name '*.cs' | wc -l | tr -d ' ') .cs files under $STAGE/Doc"
