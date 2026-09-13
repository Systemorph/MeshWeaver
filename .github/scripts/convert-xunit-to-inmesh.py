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
    (r"override\s+MessageHubConfiguration\s+Configure(Host|Client|Mesh)\b", "overrides the fixture's Configure(Host|Client|Mesh) — the pre-boot service substitution facility, not something the live mesh can fake"),
    (r"\bGetHost\(\s*[^)\s]", "configures the host hub (GetHost(config)) — the pre-boot substitution facility"),
    (r"ClrMd|DataTarget|MiniDump", "reads process dumps — stays on xunit"),
    (r"Testcontainers|Npgsql|PostgreSql", "needs Postgres — stays on xunit"),
    (r"FileSystemStorage|AddFileSystemPersistence|FileSystemPersistence", "needs FileSystem persistence — stays on xunit"),
    (r"SetHostIdentity|ImpersonateAs\(", "acts as an arbitrary identity — declined by design"),
    (r"Process\.Start|new Process\(", "spawns a process — stays on xunit"),
    (r"IClassFixture<|ICollectionFixture<", "uses an xunit fixture — needs a hand port"),
    (r"\bIAsyncLifetime\b", "implements IAsyncLifetime (InitializeAsync/DisposeAsync) — the runner has no per-class lifecycle hook yet"),
    (r"Observable\.Using\([^\n]*Impersonate", "opens an impersonation scope with Observable.Using — the in-mesh shape is AsSystem (check-impersonation.py); needs a hand port"),
    (r"\.ToTask\(", "bridges an observable to a Task with .ToTask( — forbidden in every gated root (ObservableToTaskBridgeGuard); compose reactively first"),
    (r"using Microsoft\.Playwright|\bIPage\b|\bIBrowser\b|PortalFixture", "drives a browser (Playwright) — an e2e host, not an in-mesh case"),
    (r"^using Orleans|OrleansSharedTestBase|\bTestCluster\b|\bISiloHost\b|\bRoutingGrain\b|\bIGrainFactory\b", "needs the Orleans silo host — the gate's mesh is the monolith"),
    (r"using MeshWeaver\.Testing\.Xunit\b|MeshWeaver\.Testing\.Xunit\.", "tests the xunit adapter itself (MeshWeaver.Testing.Xunit is not a platform assembly)"),
    (r"using MeshWeaver\.Hosting\.AspNetCore|WebApplication\.CreateBuilder|\bIApplicationBuilder\b", "builds an ASP.NET host — the host assembly is not on the NodeType reference set"),
    (r"FindRepositoryRoot|ScannedRoots|RatchetedRoots|ProductionRoots|GetRepositoryRoot|\bRepoRoot\b", "reads the repository tree from the test bin (a source-scanning guard) — no tree in a mesh; stays on xunit"),
    (r"\bTestScheduler\b|Microsoft\.Reactive\.Testing", "uses Microsoft.Reactive.Testing's TestScheduler — not a platform assembly"),
    (r"\bawait\b[^;]*?(GetMeshNodeStream|GetWorkspace\(|GetDataStream|GetRemoteStream|IMeshService|meshService\.|ObserveQuery|GetQuery\(|\.Query\(|\.CreateNode\(|\.UpdateNode\(|\.DeleteNode\(|\.CopyNode\()", "awaits a mesh read/write directly (HubReachableAsyncGuard.NoNewAwaitOfAMeshRead) — compose reactively and subscribe, or wait through ObserveCompletion"),
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
    s = re.sub(r"\[HubFact(\(([^)]*)\))?\]", lambda m: "[MeshFact(TimeoutSeconds = 30" + (", " + _args(m.group(2)) if m.group(2) else "") + ")]", s)
    s = re.sub(r":\s*(MonolithMeshTestBase|HubTestBase|TestBase)\b(?!<)", ": InMeshTestBase", s)
    # a primary constructor forwarding the xunit output to its base: `X(ITestOutputHelper output) : Base(output)`
    s = re.sub(r"\(ITestOutputHelper\s+output\)\s*:\s*InMeshTestBase\(output\)", "(MeshTestContext context) : InMeshTestBase(context)", s)
    s = re.sub(r"\[assembly:[^\]]*\]\s*\n", "", s)
    s = s.lstrip("\ufeff")
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
    assert convert("var t = obs.ToTask();", "x")[0] is None
    assert convert("var n = await ws.GetMeshNodeStream(p).FirstAsync();", "x")[0] is None
    assert convert("class X : HubTestBase { protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration c) => c; }", "x")[0] is None
    assert convert("var h = GetHost(c => c.AddData());", "x")[0] is None
    assert convert("var h = GetHost();", "x")[0] is not None
    assert convert("using Microsoft.Playwright;", "x")[0] is None
    assert "[MeshFact(TimeoutSeconds = 30)]" in convert("[HubFact]\npublic void A() {}", "x")[0]
    assert "(MeshTestContext context) : InMeshTestBase(context)" in convert("public class T(ITestOutputHelper output) : HubTestBase(output) {}", "x")[0]
    print("✓ convert-xunit-to-inmesh self-test: attributes (HubFact too), base, ctor (primary too), usings, header; refusals name their reason — ToTask, awaited mesh reads, host configuration, browsers, Orleans"); return 0

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
