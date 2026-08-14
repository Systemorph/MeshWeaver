#!/bin/bash
set -euo pipefail
SRC="$1"
STAGE="$2"
rm -rf "$STAGE"
mkdir -p "$STAGE"
# The domain trees that carry runtime-compiled Source/*.cs and NodeType definitions. Infra/fixture
# partitions (welcome, login, Admin, Agent, Type, ApiToken, VUser, Doc, MeshWeaver, TestSpace,
# Brand, Architecture) are deliberately not gated — they hold no compilable content.
# User is NOT gated: the gate mesh serves 'User' from a static node provider (AddMeshNodes), and
# installing over a static-provider-served path is refused by design (MeshWeaver#1209).
for name in ACME Northwind Cornerstone FutuRe PensionFund MathDemo PythonDemo Systemorph; do
  if [ ! -d "$SRC/$name" ]; then echo "::error::samples gate: expected tree '$name' is missing"; exit 1; fi
  mkdir -p "$STAGE/$name"
  cp -R "$SRC/$name/." "$STAGE/$name/"
  rm -f "$STAGE/$name/index.md"
  # Uniform synthesized Space root: the FileSystem layout keeps roots as SIBLING jsons carrying
  # persistence stamps (version/createdDate) that have no business in a fresh gate install.
  printf '%s' "{\"\$type\":\"MeshNode\",\"id\":\"$name\",\"path\":\"$name\",\"mainNode\":\"$name\",\"name\":\"$name\",\"nodeType\":\"Space\",\"state\":\"Active\"}" > "$STAGE/$name/index.json"
done
echo "staged: $(find "$STAGE" -name '*.cs' | wc -l | tr -d ' ') .cs files, $(ls "$STAGE" | wc -l | tr -d ' ') packages"
