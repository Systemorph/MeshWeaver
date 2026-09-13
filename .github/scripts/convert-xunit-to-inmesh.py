#!/usr/bin/env python3
"""convert-xunit-to-inmesh.py — turn an xunit test file of the monolith estate into an in-mesh test
source (Testing/PlatformSuite, MeshWeaver.Testing.InMesh). Maintainer, 2026-09-13: "pls refactor 100%
to this shape". Mechanical, and honest about what it cannot do: a file that overrides ConfigureMesh,
uses a second process, ClrMd, Testcontainers/Postgres/FileSystem persistence or an arbitrary identity
is REFUSED with the reason (those need a facility, or stay on xunit — Hosting/InMeshTestMigration.md).

  usage: convert-xunit-to-inmesh.py <src.cs> <dest-dir> [--id <NodeId>]   (one file)
         convert-xunit-to-inmesh.py --self-test

Transforms: [Fact]→[MeshFact], [Theory]→[MeshTheory], [InlineData]→[MeshInlineData]; `using Xunit`
→ `using MeshWeaver.Testing.InMesh`; FluentAssertions → MeshWeaver.Reactive.Assertions; the class's
base `MonolithMeshTestBase` → `InMeshTestBase` and its `(ITestOutputHelper output) : base(output)`
constructor → `(MeshTestContext context) : base(context)`; [Collection]/[CollectionDefinition] and
the `namespace …;` line are dropped (in-mesh sources are one compilation); a `<meshweaver>` header
names the node. Everything else is kept byte for byte — the assertion vocabulary is portable.
"""
from __future__ import annotations
import re, sys, pathlib

REFUSE = [
    (r"override\s+MeshBuilder\s+ConfigureMesh", "overrides ConfigureMesh — needs the per-suite configuration facility"),
    (r"ClrMd|DataTarget|MiniDump", "reads process dumps — stays on xunit"),
    (r"Testcontainers|Npgsql|PostgreSql", "needs Postgres — stays on xunit"),
    (r"FileSystemStorage|AddFileSystemPersistence|FileSystemPersistence", "needs FileSystem persistence — stays on xunit"),
    (r"SetHostIdentity|ImpersonateAs\(", "acts as an arbitrary identity — declined by design"),
    (r"Process\.Start|new Process\(", "spawns a process — stays on xunit"),
    (r"IClassFixture<|ICollectionFixture<", "uses an xunit fixture — needs a hand port"),
    (r"Observable\.Using\([^\n]*Impersonate", "opens an impersonation scope with Observable.Using — the in-mesh shape is AsSystem (check-impersonation.py); needs a hand port"),
]

def convert(text: str, node_id: str) -> tuple[str | None, str]:
    for pat, why in REFUSE:
        if re.search(pat, text):
            return None, why
    s = text
    s = re.sub(r"^using Xunit(\.[\w.]+)?;\n", "", s, flags=re.M)
    s = re.sub(r"^using FluentAssertions(\.[\w.]+)?;\n", "", s, flags=re.M)
    s = re.sub(r"^using MeshWeaver\.Fixture;\n", "", s, flags=re.M)
    s = re.sub(r"^using MeshWeaver\.Hosting\.Monolith\.TestBase;\n", "", s, flags=re.M)
    s = re.sub(r"^namespace [\w.]+;\n\n?", "", s, flags=re.M)
    s = re.sub(r"^\s*\[CollectionDefinition\([^\]]*\)\]\s*\n", "", s, flags=re.M)
    s = re.sub(r"^\s*\[Collection\([^\]]*\)\]\s*\n", "", s, flags=re.M)
    s = re.sub(r"\[Fact(\(([^)]*)\))?\]", lambda m: "[MeshFact" + (f"({_args(m.group(2))})" if m.group(2) else "") + "]", s)
    s = re.sub(r"\[Theory(\(([^)]*)\))?\]", lambda m: "[MeshTheory" + (f"({_args(m.group(2))})" if m.group(2) else "") + "]", s)
    s = s.replace("[InlineData(", "[MeshInlineData(")
    s = re.sub(r":\s*(MonolithMeshTestBase|HubTestBase|TestBase)\b(?!<)", ": InMeshTestBase", s)
    s = re.sub(r"\(ITestOutputHelper\s+output\)\s*:\s*base\(output\)", "(MeshTestContext context) : base(context)", s)
    s = re.sub(r"\(ITestOutputHelper\s+output\)", "(MeshTestContext context)", s)
    s = s.replace("TestContext.Current.CancellationToken", "CancellationToken.None")
    # `access.RunAsSystem(() => work)` latches the identity across threads in-mesh (core#1820): the base's
    # AsSystem(access, () => work) is the sanctioned Observable.Create shape.
    s = re.sub(r"\b([A-Za-z_][A-Za-z0-9_.]*)\.RunAsSystem\(", r"AsSystem(\1, ", s)
    s = re.sub(r"^using Xunit\.Abstractions;\n", "", s, flags=re.M)
    header = (f"// <meshweaver>\n// Id: {node_id}\n// DisplayName: {node_id} — migrated from xunit (convert-xunit-to-inmesh.py)\n// </meshweaver>\n"
              "#nullable enable\nusing MeshWeaver.Reactive.Assertions;\nusing MeshWeaver.Testing.InMesh;\n")
    if "using System;" not in s:
        header += "using System;\n"
    return header + s, "ok"

def _args(a: str) -> str:
    # xunit's Timeout = milliseconds → TimeoutSeconds; Skip/DisplayName pass through
    out = []
    for part in [p.strip() for p in a.split(",") if p.strip()]:
        m = re.match(r"Timeout\s*=\s*(\d+)", part)
        out.append(f"TimeoutSeconds = {max(1, int(m.group(1)) // 1000)}" if m else part)
    return ", ".join(out)

def self_test() -> int:
    src = '''using FluentAssertions;
using MeshWeaver.Graph;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

[Collection("SamplesGraphData")]
public class SampleTest : MonolithMeshTestBase
{
    public SampleTest(ITestOutputHelper output) : base(output) { }

    [Fact(Timeout = 30000)]
    public async Task Reads() { var n = await ReadNode("x"); n.Should().NotBeNull(); }

    [Theory]
    [InlineData(1)]
    public void Adds(int a) { a.Should().Be(1); }
}
'''
    out, why = convert(src, "Testing/PlatformSuite/Sample")
    assert out is not None, why
    for must in ["[MeshFact(TimeoutSeconds = 30)]", "[MeshTheory]", "[MeshInlineData(1)]", ": InMeshTestBase", "(MeshTestContext context) : base(context)",
                 "using MeshWeaver.Testing.InMesh;", "using MeshWeaver.Reactive.Assertions;", "// Id: Testing/PlatformSuite/Sample"]:
        assert must in out, must
    for never in ["using Xunit", "FluentAssertions;", "[Collection(", "namespace ", "ITestOutputHelper"]:
        assert never not in out, never
    assert convert("class X : MonolithMeshTestBase { protected override MeshBuilder ConfigureMesh(MeshBuilder b) => b; }", "x")[0] is None
    assert convert("using Testcontainers.PostgreSql;", "x")[0] is None
    assert convert("var x = Observable.Using(() => access.ImpersonateAsSystem(), _ => y);", "x")[0] is None
    assert "AsSystem(access, () => Mesh.CreateNode(n))" in convert("var r = access.RunAsSystem(() => Mesh.CreateNode(n));", "x")[0]
    print("✓ convert-xunit-to-inmesh self-test: attributes, base, ctor, usings, header; refusals name their reason"); return 0

def main() -> int:
    if "--self-test" in sys.argv: return self_test()
    if len(sys.argv) < 3: print(__doc__); return 2
    src = pathlib.Path(sys.argv[1]); dest = pathlib.Path(sys.argv[2]); dest.mkdir(parents=True, exist_ok=True)
    node_id = sys.argv[sys.argv.index("--id") + 1] if "--id" in sys.argv else src.stem
    out, why = convert(src.read_text(encoding="utf-8"), node_id)
    if out is None:
        print(f"REFUSED {src.name}: {why}"); return 1
    (dest / src.name).write_text(out, encoding="utf-8"); print(f"converted {src.name} → {dest / src.name}"); return 0

if __name__ == "__main__": sys.exit(main())
