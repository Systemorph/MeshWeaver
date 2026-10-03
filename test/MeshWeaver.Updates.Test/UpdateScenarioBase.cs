using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 The harness every update scenario shares: a REAL mesh, a NodeType compiled from a real
/// <c>Code</c> node by the real pipeline, an instance hub activated on it, and one observable fact
/// about which build that hub is serving — the marker its <c>Overview</c> area renders. Each build
/// renders <c>MARKER_{n}</c>, so "which version is bound" is read off the running address, not off
/// a record that could disagree with it (#2471 is exactly a record that said Ok over stale bytes).
///
/// <para>No sleeps anywhere: every wait is a framework read — the NodeType's own stream for a
/// publication, <see cref="HubRecycleExtensions.RecycleNode"/> for a re-activation (it emits once
/// the FRESH activation answered), the remote layout stream for what a hub serves.</para>
/// </summary>
public abstract class UpdateScenarioBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Generous for a CI cold start; a healthy step takes well under a second.</summary>
    protected static readonly TimeSpan Step = TimeSpan.FromSeconds(45);

    /// <summary>Per-test suffix: every scenario owns its own type, so no two share a store key.</summary>
    protected readonly string Suffix = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The C# a build of the probe type is compiled from: one area that names the build.</summary>
    protected static string Code(string marker) => $$"""
        using MeshWeaver.Layout.Composition;
        public static class UpdateProbeAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_{{marker}}</div>");
        }
        """;

    /// <summary>Source that does not compile — a failed N+1.</summary>
    protected const string BrokenCode = """
        using MeshWeaver.Layout.Composition;
        public static class UpdateProbeAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => this does not compile;
        }
        """;

    private const string ProbeConfiguration =
        "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", UpdateProbeAreas.Overview))";

    /// <summary>The definition the mesh hub currently sees for <paramref name="typePath"/>.</summary>
    protected Task<NodeTypeDefinition> DefinitionWhere(string typePath, Func<NodeTypeDefinition, bool> predicate, string because)
        => Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null && predicate(d))
            .Select(d => d!)
            .Take(1)
            .Should().Within(Step).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Creates the probe type with build <paramref name="marker"/> and waits until it is
    /// compiled; returns the type path and the published definition (build N).</summary>
    protected async Task<(string TypePath, NodeTypeDefinition Build)> CreateType(string marker, string? code = null)
    {
        var id = $"UpdateProbe{Suffix}";
        var typePath = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Updates-suite probe: renders the marker of the build it is bound to.",
                Configuration = ProbeConfiguration,
            },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = code ?? Code(marker), Language = "csharp" },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);

        await Release(typePath);
        var build = await DefinitionWhere(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok && !string.IsNullOrEmpty(d.LatestAssemblyPath),
            $"build {marker} must compile");
        return (typePath, build);
    }

    /// <summary>Replaces the type's source with <paramref name="code"/> and waits until the type
    /// reports the edit (IsDirty) — the state "N+1 exists as source but is not published".</summary>
    protected async Task EditSource(string typePath, string code)
    {
        var sourcePath = $"{typePath}/Source/code";
        var current = await Mesh.GetWorkspace().GetMeshNodeStream(sourcePath)
            .Where(n => n is not null).Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.UpdateNode(current! with
        {
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await DefinitionWhere(typePath, d => d.IsDirty, "the type must observe its source edit");
    }

    /// <summary>Publishes the current source as the next build and waits for the terminal state.</summary>
    protected async Task<NodeTypeDefinition> Publish(string typePath, NodeTypeDefinition previous)
    {
        await Release(typePath);
        return await DefinitionWhere(typePath,
            d => (d.CompilationStatus == CompilationStatus.Ok
                  && !string.Equals(d.LatestAssemblyPath, previous.LatestAssemblyPath, StringComparison.Ordinal))
                 || d.CompilationStatus == CompilationStatus.Error,
            "the release must reach a terminal state");
    }

    private Task Release(string typePath)
        => GetClient(c => c.AddData())
            .Observe(new CreateReleaseRequest(Force: false), o => o.WithTarget(new Address(typePath)))
            .Select(d => d.Message)
            .Take(1)
            .Should().Within(Step).Emit("the release request must be answered",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Creates an instance of the probe type; its hub activates on the first read.</summary>
    protected async Task<string> CreateInstance(string typePath, string id, object? content = null)
    {
        await NodeFactory.CreateNode(new MeshNode(id, typePath)
        {
            Name = id,
            NodeType = typePath,
            Content = JsonSerializer.SerializeToElement(content ?? new { note = $"written under {id}" }),
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return $"{typePath}/{id}";
    }

    /// <summary>
    /// What the hub at <paramref name="instancePath"/> serves RIGHT NOW: the first rendered
    /// Overview of a fresh subscription. A fresh subscription is answered by the live activation,
    /// so this reads the bound build without causing a re-activation.
    /// </summary>
    protected Task<string> ServedMarker(string instancePath)
    {
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        return workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference)
            .GetControlStream(reference.Area!)
            .OfType<HtmlControl>()
            .Select(h => h.Data?.ToString() ?? string.Empty)
            .Where(html => html.Contains("MARKER_", StringComparison.Ordinal))
            .Take(1)
            .Should().Within(Step).Emit($"'{instancePath}' must serve a build (never park)",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Asserts the hub serves build <paramref name="marker"/>.</summary>
    protected async Task AssertServes(string instancePath, string marker, string because)
    {
        var html = await ServedMarker(instancePath);
        html.Should().Contain($"MARKER_{marker}<", because);
    }

    /// <summary>Posts the DisposeRequest and waits until the FRESH activation answered.</summary>
    protected Task Recycle(string instancePath)
        => GetClient(c => c.AddData())
            .RecycleNode(instancePath, Step, reason: "Updates suite: re-activate to bind the newest build")
            .Take(1)
            .Should().Within(Step + Step).Emit("the recycled address must answer again",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The store this mesh resolves builds through.</summary>
    protected IAssemblyStore Store => Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();

    /// <summary>Writes the build fields of <paramref name="typePath"/> through its owner — the same
    /// field set a prebuilt-bundle adoption stamps (PrebuiltAssemblySeeder) — and waits until the
    /// mesh hub sees them.</summary>
    protected async Task<NodeTypeDefinition> StampBuild(
        string typePath, Func<NodeTypeDefinition, NodeTypeDefinition> stamp, Func<NodeTypeDefinition, bool> landed)
    {
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath).Update(curr =>
        {
            var def = curr?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions);
            return def is null ? curr! : curr! with { Content = stamp(def) };
        }).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return await DefinitionWhere(typePath, landed, "the stamped build must reach the mesh hub");
    }
}
