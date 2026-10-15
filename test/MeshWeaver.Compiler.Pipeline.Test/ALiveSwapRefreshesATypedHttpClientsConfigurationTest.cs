using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A module's NAMED HttpClient configuration (<c>AddHttpClient&lt;T&gt;(configure)</c>), served from the ROOT
/// <see cref="IHttpClientFactory"/> — the half the probe fix (#6652, MeshWeaver.Plugins#3518) left open. Two
/// defects, each pinned by its own test:
///
/// <list type="number">
/// <item><b>Dropped at boot.</b> The registration is a <c>ConfigureNamedOptions&lt;HttpClientFactoryOptions&gt;</c>,
/// and the root held it behind a <c>DispatchProxy</c> of <c>IConfigureOptions&lt;T&gt;</c> only. The options
/// factory applies a setup that is not an <c>IConfigureNamedOptions&lt;T&gt;</c> to the DEFAULT name only, so
/// every named client a module configured was handed out bare. Negative control: with the
/// <c>ModuleConfigureOptionsForwarder</c> case removed from <c>ModuleServiceForwarding.AddForwarders</c>, the
/// client carries no <c>X-Generation</c> header at all (measured: <c>found &lt;null&gt;</c>).</item>
/// <item><b>Stale after a swap.</b> The root's <c>IOptionsMonitor&lt;HttpClientFactoryOptions&gt;</c> caches the
/// named options built from generation N. Negative control: with <c>ModuleContexts.EvictRootOptions</c> not
/// called from <c>CommitAll</c>, the client after the swap still carries generation N's header.</item>
/// </list>
/// </summary>
public sealed class ALiveSwapRefreshesATypedHttpClientsConfigurationTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.LiveHttpClient";
    private const string ClientName = "LiveSearchClient";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-http-" + Guid.NewGuid().ToString("N"));
    private string? v1Path;
    private string V1 => v1Path ??= Write("g1", ModuleSource(1));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddHttpClient())
            .InstallAssemblies(V1);

    [Fact]
    public void AModulesNamedClientConfiguration_ReachesTheRootFactory()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        Generation(factory.CreateClient(ClientName)).Should().Be("v1",
            "the module configured its named client, and the root factory must apply that configuration");
    }

    [Fact(Timeout = 180_000)]
    public async Task ATypedClientResolvedBeforeTheSwap_CarriesTheNewGenerationsConfigurationAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Mesh.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        Generation(factory.CreateClient(ClientName)).Should().Be("v1",
            "the arrangement: generation N's configuration is applied, and its options are now cached");

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", ModuleSource(2)), "test: typed HttpClient configuration").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        Generation(factory.CreateClient(ClientName)).Should().Be("v2",
            "after a live swap the named client's options must be rebuilt from N+1's configuration — "
            + "options cached from generation N would keep N's delegates serving");
    }

    /// <summary>
    /// <see cref="ModuleContexts.VersionChanged"/> announces that N+1 is live, synchronously; a subscriber that
    /// resolves the named client inside that notification must already get N+1's configuration. Negative
    /// control: with <c>EvictRootOptions</c> called after <c>Bump()</c> in <c>CommitAll</c>, the client
    /// resolved in the notification still carries "v1".
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AClientResolvedInsideTheVersionNotification_CarriesTheNewGenerationsConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Mesh.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        Generation(factory.CreateClient(ClientName)).Should().Be("v1",
            "the arrangement: generation N's configuration is applied, and its options are now cached");

        string? seenInNotification = null;
        using var subscription = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().VersionChanged
            .Subscribe(_ => Volatile.Write(ref seenInNotification, Generation(factory.CreateClient(ClientName))));

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>()
            .Swap(Write("g2", ModuleSource(2)), "test: resolve inside VersionChanged").Timeout(Budget).Await(ct);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        Volatile.Read(ref seenInNotification).Should().Be("v2",
            "VersionChanged says N+1 is live — a client resolved while it is being announced must not be "
            + "built from generation N's cached options");
    }

    private static string? Generation(HttpClient client) =>
        client.DefaultRequestHeaders.TryGetValues("X-Generation", out var values) ? values.Single() : null;

    private static string ModuleSource(int version) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.LiveHttpClient.Module]
        namespace MeshWeaver.Test.LiveHttpClient;
        public sealed class LiveSearchClient(System.Net.Http.HttpClient http) { public System.Net.Http.HttpClient Http { get; } = http; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("LiveHttpClientProbe") { Name = "v{{version}}", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddHttpClient<LiveSearchClient>(c => c.DefaultRequestHeaders.Add("X-Generation", "v{{version}}"));
                        return services;
                    })];
        }
        """;

    private string Write(string generation, string source)
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
