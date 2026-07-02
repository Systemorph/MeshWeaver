#!/usr/bin/env bash
# Generate the Python gRPC stubs from the canonical mesh.proto.
# The proto lives with the C# server so there is ONE contract, no copies.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pkg_root="$(dirname "$here")"                 # clients/python
repo_root="$(cd "$pkg_root/../.." && pwd)"    # repo root
proto_dir="$repo_root/src/MeshWeaver.Hosting.Grpc/Protos"
out_dir="$pkg_root/meshweaver/_generated"

mkdir -p "$out_dir"
touch "$out_dir/__init__.py"

"${PYTHON:-python3}" -m grpc_tools.protoc \
    -I "$proto_dir" \
    --python_out="$out_dir" \
    --grpc_python_out="$out_dir" \
    "$proto_dir/mesh.proto"

# grpc_tools emits `import mesh_pb2` (flat) — rewrite to a package-relative import.
sed -i.bak 's/^import mesh_pb2/from . import mesh_pb2/' "$out_dir/mesh_pb2_grpc.py" && rm -f "$out_dir/mesh_pb2_grpc.py.bak"

echo "Generated stubs in $out_dir"
