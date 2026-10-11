#!/usr/bin/env bash
#
# Build the /next front end that rides INSIDE the platform portal image.
#
#   .github/scripts/build-portal-next-bundle.sh <plugins-checkout> <out.zip>
#
# 🚂 /next IS ON THE PLATFORM RELEASE TRAIN (policy `portal-next-in-platform-image`). It used to be a separate image,
# `memex-portal-next:3.0.0-next.<run>`, built by a lane of its own in MeshWeaver.Plugins, excluded by
# name from this repo's all-or-nothing image set, never promoted by release.yml and never moved by a
# roll — so every deployment that enabled it PINNED it, and the pin drifted from the portal it talks
# to. Now this script builds it in the portal-ai leg, the publish lays it into memex-portal-ai
# (`-p:PortalNextBundle=<out.zip>` → /app/portal-next.zip), and the portal process serves it. One
# image, one version, one `3-latest` pointer.
#
# The app and its bundling live in MeshWeaver.Plugins (clients/portal-next/scripts/bundle.sh, which
# that repo's CI runs on every pull request touching the clients — so a break is red THERE before it
# can stop this delivery). This script owns only the two things core has to supply: the protos the
# gRPC-web client generates from (this checkout), and the fail-closed assertion that a bundle exists.
#
# 🚨 NO SKIP TRAPDOOR: a missing checkout, script, node or output is RED. An image published without
# /next is exactly the drift this exists to remove.
set -euo pipefail

plugins="${1:?usage: build-portal-next-bundle.sh <plugins-checkout> <out.zip>}"
out="${2:?usage: build-portal-next-bundle.sh <plugins-checkout> <out.zip>}"
core="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

bundle="$plugins/clients/portal-next/scripts/bundle.sh"
[ -x "$bundle" ] || { echo "::error::$bundle is missing or not executable — the Plugins checkout predates the bundled /next (MeshWeaver.Plugins clients/portal-next/scripts/bundle.sh). Refusing to publish a portal image without /next."; exit 1; }
command -v node >/dev/null || { echo "::error::node is not on PATH — set up Node before this step (actions/setup-node)."; exit 1; }
command -v python3 >/dev/null || { echo "::error::python3 is not on PATH — bundle.sh writes the zip with Python's stdlib."; exit 1; }

# The gRPC-web client is GENERATED from THIS checkout's protos (src/MeshWeaver.Mesh.Contract/Protos);
# MESHWEAVER_CORE is the first place clients/scripts/resolve-core.cjs looks.
export MESHWEAVER_CORE="$core"
npm --prefix "$plugins/clients/grpc-web" ci --no-audit --no-fund
npm --prefix "$plugins/clients/grpc-web" run gen
[ -f "$plugins/clients/grpc-web/src/gen/mesh_pb.ts" ] || { echo "::error::the gRPC-web codegen produced no src/gen/mesh_pb.ts — an exit code is not an artefact."; exit 1; }

"$bundle" "$out"

[ -s "$out" ] || { echo "::error::bundle.sh exited 0 but wrote no $out."; exit 1; }
python3 - "$out" <<'PY'
import sys, zipfile
names = set(zipfile.ZipFile(sys.argv[1]).namelist())
if "portal-next/server.js" not in names:
    sys.exit(f"::error::{sys.argv[1]} holds no portal-next/server.js — not a /next bundle")
print(f"/next bundle verified: {len(names)} entries, portal-next/server.js present")
PY
