using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
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

namespace MeshWeaver.AI.Test;

/// <summary>
/// Issue #576, remaining scope 2 — the MCP compile tools must tell the TRUTH about an
/// in-flight compile.
///
/// <para>Both tools used to lie in the same situation — a compile that is genuinely running:</para>
/// <list type="bullet">
///   <item><c>Compile</c> never inspected the current status. It stamped a fresh
///     <c>RequestedReleaseAt</c> (which the release watcher's settled-gate holds anyway, so
///     NOTHING started), waited out its 60s budget and answered <c>{status:"Pending"}</c> with
///     no <c>activityPath</c> — the caller could not tell a healthy long compile from a wedged
///     one, and could not find the running activity to look at.</item>
///   <item><c>GetDiagnostics</c> waited 5s for a SETTLED status and, on timeout, fell into the
///     null-node path → <c>{status:"Unknown", message:"NodeType '…' has no definition"}</c>. The
///     definition plainly exists; its compile is RUNNING. That made the <c>Compiling</c> +
///     <c>elapsedMs</c> branch of <c>FormatDiagnostics</c> unreachable for the single state it
///     was written for.</item>
/// </list>
///
/// <para>The state is arranged the way the framework itself does it — the NodeType's own
/// <c>stream.Update</c> writing <c>CompilationStatus = Compiling</c> + <c>LastCompileStartedAt</c>
/// + <c>LastCompilationActivityPath</c>, exactly what <c>RunCompile</c>'s Compiling-flip writes —
/// so the tools are exercised against the real shape rather than a mock.</para>
/// </summary>
public class McpCompileInFlightTruthTest(ITestOutputHelper output) : AITestBase(output)
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverMcpInFlightTest-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                }));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string TypeName = "InFlightTruthType";
    private static readonly string RunningActivityPath =
        TestPartition + "/" + TypeName + "/_Activity/compile-inflight-under-test";

    [Fact(Timeout = 120000)]
    public async Task InFlightCompile_CompileReportsIt_AndGetDiagnosticsSaysCompiling()
    {
        var ops = new MeshOperations(Mesh);
        var typePath = $"{TestPartition}/{TypeName}";

        // 1. A real NodeType, compiled once so the type is live and settled.
        await NodeFactory.CreateNode(new MeshNode(TypeName, TestPartition)
        {
            Name = "In Flight Truth Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Fixture for the MCP in-flight-compile truthfulness contract (#576).",
                Configuration = "config => config.AddDefaultLayoutAreas()",
            }
        }).Should().Within(30.Seconds()).Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
        Output.WriteLine("=== first build settled Ok ===");

        // 2. Put the type into the exact state RunCompile's Pending→Compiling flip writes: a
        //    compile IS running, its activity node is recorded, and the start instant is stamped.
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with
            {
                Content = def with
                {
                    CompilationStatus = CompilationStatus.Compiling,
                    LastCompileStartedAt = startedAt,
                    LastCompilationActivityPath = RunningActivityPath,
                }
            };
        }).Should().Within(30.Seconds()).Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Compiling);
        var beforeTrigger = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds()).Emit();
        var beforeDef = (NodeTypeDefinition)beforeTrigger.Content!;

        // 3. 🚨 Compile must ANSWER, not wait out its budget: "already compiling", with the
        //    activity to watch — and it must NOT start a second run.
        var compileJson = await ops.Compile($"@{typePath}").Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"=== compile → {compileJson} ===");
        using (var doc = JsonDocument.Parse(compileJson))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Compiling",
                "a compile that is in flight must be reported as Compiling, never as a "
                + "timed-out Pending");
            root.GetProperty("activityPath").GetString().Should().Be(RunningActivityPath,
                "the caller must get the RUNNING compile's activity to watch — that is the "
                + "whole point of answering instead of timing out");
            root.GetProperty("elapsedMs").GetInt64().Should().BeGreaterThan(0,
                "elapsed time is how a caller judges 'healthy long compile' vs 'wedged'");
            root.GetProperty("message").GetString().Should().Contain("ALREADY IN FLIGHT");
            root.GetProperty("message").GetString().Should().Contain("did NOT",
                "the answer must state plainly that no second run was started");
        }

        // 4. …and it really did not trigger: the single-flight lock already guarantees no second
        //    RUN, but a stamped trigger would queue a redundant compile behind the running one.
        var afterTrigger = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(30.Seconds()).Emit();
        var afterDef = (NodeTypeDefinition)afterTrigger.Content!;
        afterDef.RequestedReleaseAt.Should().Be(beforeDef.RequestedReleaseAt,
            "Compile must not stamp a release request while a compile is in flight");
        afterDef.CompilationStatus.Should().Be(CompilationStatus.Compiling);

        // 5. 🚨 GetDiagnostics on the same in-flight type must report Compiling (with elapsed),
        //    NOT "Unknown / has no definition".
        var diagJson = await ops.GetDiagnostics($"@{typePath}")
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"=== get_diagnostics → {diagJson} ===");
        using (var doc = JsonDocument.Parse(diagJson))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Compiling",
                "the settle-wait timing out means the compile is RUNNING — reporting 'Unknown / "
                + "has no definition' for a node whose definition is right there is a lie");
            root.GetProperty("elapsedMs").GetInt64().Should().BeGreaterThan(0);
            root.GetProperty("message").GetString().Should().Contain("IN PROGRESS");
        }
    }

    /// <summary>
    /// Pure-wording lock on the already-in-flight envelope (sibling of the
    /// <c>FormatDiagnostics</c> wording tests): the three facts a caller needs — a compile IS
    /// running, this call started nothing, here is where to watch — must all be present, and the
    /// no-activity-recorded variant must degrade to a poll instruction rather than a null path.
    /// </summary>
    [Fact]
    public void FormatAlreadyCompiling_CarriesRunningActivityAndNoSecondRunStatement()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var withActivity = MeshOperations.FormatAlreadyCompiling(
            "ACME/SomeType", "ACME/SomeType/_Activity/compile-1",
            DateTimeOffset.UtcNow.AddSeconds(-5), options);
        using (var doc = JsonDocument.Parse(withActivity))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Compiling");
            root.GetProperty("activityPath").GetString()
                .Should().Be("ACME/SomeType/_Activity/compile-1");
            // 🚨 Not >= 5000: elapsed is (UtcNow - startedAt) TRUNCATED to a long, and UtcNow has
            // advanced by microseconds between the seed and the call — 4999 is a legitimate
            // outcome, so an exact-boundary assertion is a latent flake. Assert the ORDER OF
            // MAGNITUDE the field is meant to convey (a multi-second run), not the boundary.
            root.GetProperty("elapsedMs").GetInt64().Should().BeGreaterThan(4000);
            var message = root.GetProperty("message").GetString()!;
            message.Should().Contain("ALREADY IN FLIGHT");
            message.Should().Contain("did NOT");
            message.Should().Contain("ACME/SomeType/_Activity/compile-1");
        }

        var withoutActivity = MeshOperations.FormatAlreadyCompiling(
            "ACME/SomeType", activityPath: null, startedAt: null, options);
        using (var doc = JsonDocument.Parse(withoutActivity))
        {
            var root = doc.RootElement;
            root.GetProperty("status").GetString().Should().Be("Compiling");
            root.GetProperty("activityPath").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("elapsedMs").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("message").GetString()
                .Should().Contain("No activity node was recorded",
                    "with nothing to watch the caller must be told to poll, not handed a null path");
        }
    }
}
