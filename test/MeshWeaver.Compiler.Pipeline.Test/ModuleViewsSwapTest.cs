using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>View packs go live (policy <c>module-live-update-default</c>).</b> A module's view registrations
/// declared through <see cref="MeshNodeProviderAttribute.Views"/> are re-read by the layout client from the
/// module's CURRENT generation (<see cref="IViewContributionSource"/>) — so after a live swap, a client
/// resolves the control's view to the NEW generation's view type, with no restart. The negative control:
/// the same registration folded into the mesh hub through <c>HubConfigurations</c>' <c>AddViews</c> is a
/// named blocker, because the mesh hub's configuration is built once.
/// </summary>
public sealed class ModuleViewsSwapTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveViews";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-views-" + Guid.NewGuid().ToString("N"));
    private string? v1Path;
    private string V1 => v1Path ??= Write("g1", ModuleSource(1));

    public ModuleViewsSwapTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(V1);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    [Fact(Timeout = 180_000)]
    public async Task AModulesViews_AreResolvedFromTheNewGeneration_AfterALiveSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        var contributions = Contexts.Current(Module)?.Contributions
            ?? throw new Xunit.Sdk.XunitException($"{Module} is not held, or its contributions were never recorded");
        contributions.LiveUpdateBlockers().Should().BeEmpty(
            "views declared through Views must not block a live swap");
        var client = GetClient(c => c.AddLayoutClient()).ServiceProvider.GetRequiredService<ILayoutClient>();
        ViewVersion(client).Should().Be(1);

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", ModuleSource(2)), "test: views").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        ViewVersion(client).Should().Be(2,
            "the SAME layout client must resolve the control to the new generation's view — re-read, not folded once");
    }

    [Fact]
    public void ViewsFoldedIntoTheMeshHubThroughAddViews_AreABlocker()
    {
        using var contexts = new ModuleContexts();
        var generation = contexts.Load(Write("gx", ModuleSource(1, throughMeshHub: true)));
        contexts.Commit(generation);

        ModuleContributions.Of(generation.Assembly).MeasuredLiveUpdateBlockers()
            .Should().Contain(b => b.Contains("configures the mesh hub beyond its type registry"));
    }

    private static int ViewVersion(ILayoutClient client)
    {
        var descriptor = client.GetViewDescriptor("live-views-probe", null, "area");
        descriptor.Should().NotBeNull("the module's view map must accept the probe");
        return descriptor?.Type.GetProperty("Version")?.GetValue(null) is int version
            ? version
            : throw new Xunit.Sdk.XunitException("the probe view carries no static int Version");
    }

    private static string ModuleSource(int version, bool throughMeshHub = false) => $$"""
        [assembly: MeshWeaver.Test.LiveViews.Module]
        namespace MeshWeaver.Test.LiveViews;
        public sealed class ProbeView { public static int Version => {{version}}; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            private static MeshWeaver.Layout.Client.LayoutClientConfiguration Register(MeshWeaver.Layout.Client.LayoutClientConfiguration views) =>
                views.WithView((instance, stream, area) => instance is "live-views-probe"
                    ? new MeshWeaver.Layout.Client.ViewDescriptor(typeof(ProbeView), new System.Collections.Generic.Dictionary<string, object?>())
                    : null);
            {{(throughMeshHub
                ? "public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations => [config => MeshWeaver.Layout.LayoutExtensions.AddViews(config, Register)];"
                : "public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Layout.Client.LayoutClientConfiguration, MeshWeaver.Layout.Client.LayoutClientConfiguration>> Views => [Register];")}}
        }
        """;

    private string Write(string generation, string source)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
