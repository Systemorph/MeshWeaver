using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A COMPILE READS ITS OWN SOURCE AS SYSTEM — issue #1253.
///
/// <para>An <c>@@</c> code include is resolved by a <c>GetMeshNode</c> read the compiler issues on
/// its own behalf. It carried no identity of its own, so it depended on whatever ambient
/// <c>AccessService</c> context happened to be present at subscribe — and that context is an
/// <c>AsyncLocal</c>. On the activation/bake path there is often no user at all, and on Orleans
/// there is no circuit fallback to borrow one from. The read then posted a <c>GetDataRequest</c>
/// with a null <c>AccessContext</c>, the never-null PostPipeline guard REFUSED it
/// (<c>d.Failed(reason)</c> — <c>GetDataRequest</c> is not exempt), and because the single-argument
/// <c>Failed</c> records no <c>ErrorType</c>, <c>GetMeshNode</c> took its non-<c>Unauthorized</c>
/// branch and emitted <b>null</b> — indistinguishable from "the node does not exist".</para>
///
/// <para>This asserts a successful COMPILE rather than merely a successful read, because that is
/// where the cost lands: an unresolved include is left VERBATIM in the source, so Roslyn parses the
/// <c>@@</c> line itself and the NodeType parks at <c>CompileError</c> — which refuses portal
/// readiness and holds every instance hub for the full 60s activation budget. memex-cloud logged
/// this on 2026-08-12 as 22 refused reads inside a single millisecond, whose targets are the five
/// <c>@@</c> lines of <c>FutuRe/LocalAnalysis/Source/ExternalDependencies</c> in file order.</para>
///
/// <para>🚨 The compile is driven with an explicit <c>sourcesOverride</c> — the shape
/// <c>HandleCreateRelease</c> uses — and that is load-bearing, not incidental. Without it the
/// snapshot comes from <c>GetSourceCollection</c>, which already wraps its query in an
/// <c>ImpersonateAsSystem</c> <c>Observable.Using</c>; on a warm Monolith that query replays
/// SYNCHRONOUSLY, so the whole downstream chain — the include read included — runs inside a System
/// scope it never asked for and the defect is invisible. (Measured: with a synchronous snapshot the
/// unfixed code passes.) That inherited scope is exactly what production does not have, because
/// there the snapshot arrives on a later thread. Overriding the snapshot removes the borrowed scope
/// and leaves the include read standing on its own identity — which is the invariant under
/// test.</para>
/// </summary>
public class CompileIncludeSystemScopeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private const string NodeTypePath = "type/IncludeCtx";

    /// <summary>
    /// The include target lives OUTSIDE <c>{NodeType}/Source</c> on purpose: the default source
    /// query is <c>namespace:{selfPath}/Source scope:subtree</c>, so discovery cannot see it and
    /// the overridden snapshot does not contain it. The ONLY route by which
    /// <c>IncludeCtxHelper</c> can reach Roslyn is the <c>@@</c> include — which makes "the
    /// assembly compiled" a direct assertion that the include read succeeded.
    /// </summary>
    private const string HelperPath = $"{NodeTypePath}/Shared/Helper";

    [Fact(Timeout = 120_000)]
    public async Task CodeInclude_ResolvesOnCompilePath_WhenNoAmbientIdentity()
    {
        // ── Seed under a real identity (the test base's DevLogin admin) ────────────────────
        var typeNode = MeshNode.FromPath(NodeTypePath) with
        {
            Name = "IncludeCtx",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<IncludeCtx>()"
            },
            State = MeshNodeState.Active
        };
        await MeshService.CreateNode(typeNode).Should().Within(30.Seconds()).Emit();

        await MeshService.CreateNode(new MeshNode("Helper", $"{NodeTypePath}/Shared")
        {
            NodeType = "Code",
            Name = "Helper",
            Content = new CodeConfiguration
            {
                Code = "public static class IncludeCtxHelper { public const int Answer = 42; }",
                Language = "csharp"
            },
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        // The ONLY source. Its first line is the include; the record below cannot compile unless
        // the include resolved and brought IncludeCtxHelper with it.
        var modelNode = await MeshService.CreateNode(new MeshNode("model", $"{NodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = "model",
            Content = new CodeConfiguration
            {
                Code = $"@@{HelperPath}\n\n"
                       + "public record IncludeCtx { public int Answer => IncludeCtxHelper.Answer; }",
                Language = "csharp"
            },
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        // ── Remove every ambient identity, then compile ───────────────────────────────────
        // 🚨 The Monolith's durable test-login fallback (AccessService.hostIdentity, set by
        // DevLogin) is resolved by EVERY hub's post pipeline, so locally the include read would
        // borrow it and the defect would be invisible. A grain has no such fallback. Clearing it
        // on the MESH's AccessService — the singleton every hosted hub shares, including the
        // portal/nodeops-… hub that actually issues the read — reproduces production exactly:
        // Context null, CircuitContext null, and no standing identity for an infrastructure hub.
        var meshAccess = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var seedIdentity = meshAccess.CircuitContext ?? meshAccess.Context;
        seedIdentity.Should().NotBeNull("the test base logs a user in as the host identity");
        meshAccess.SetHostIdentity(null);
        try
        {
            meshAccess.Context.Should().BeNull("the compile must run with no ambient identity");
            meshAccess.CircuitContext.Should().BeNull("the host-identity mask is what we removed");

            var compilationService = Mesh.ServiceProvider
                .GetRequiredService<IMeshNodeCompilationService>();
            var result = await compilationService
                .CompileAndGetConfigurations(typeNode, new List<MeshNode> { modelNode })
                .Should().Within(90.Seconds()).Emit();

            result.Should().NotBeNull();
            Output.WriteLine(Flatten(result!));

            result!.AssemblyLocation.Should().NotBeNullOrEmpty(
                "the @@ include must resolve with no ambient identity — a compile reading the "
                + "source it was asked to compile is infrastructure and reads as System. Left "
                + "unresolved, the @@ line stays VERBATIM in the source, Roslyn errors on it, and "
                + $"the NodeType parks at CompileError. {Flatten(result)}");

            // Names the actual failure mode rather than only the outcome: pre-fix the Roslyn
            // diagnostics point at the include line itself, not at anything a user wrote.
            (result.Diagnostics ?? []).Should().NotContain(
                d => d.Message.Contains("IncludeCtxHelper"),
                "an unresolved include shows up as the included type being 'not found'");

            // The compiled unit exposes its provider — proof the sources AND the Configuration
            // lambda over the included type both made it through Roslyn.
            result.NodeTypeConfigurations.Should().NotBeEmpty(
                $"the compiled assembly must expose its NodeType configuration. {Flatten(result)}");
        }
        finally
        {
            // Restore the shared identity for any later test on a shared mesh.
            meshAccess.SetHostIdentity(seedIdentity);
        }
    }

    private static string Flatten(NodeCompilationResult result)
    {
        var log = result.Log is null
            ? "(no log)"
            : string.Join(" | ", result.Log.Messages.Select(m => $"[{m.LogLevel}] {m.Message}"));
        var diagnostics = result.Diagnostics is null or { Count: 0 }
            ? "(none)"
            : string.Join(" | ", result.Diagnostics.Select(d => $"{d.Id} {d.Message}"));
        return $"log: {log} ;; roslyn: {diagnostics}";
    }
}
