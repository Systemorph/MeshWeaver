using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A root singleton whose constructor takes <see cref="IMeshService"/> — the shape of the portal's user
/// directory (<c>UserIdentityCache</c>), which a module's query provider reaches through its own
/// dependencies (MeshWeaver#7218: the Teams chats provider → its chat rooms → the member directory → the
/// user directory).
/// </summary>
public sealed class MeshServiceConsumer(IMeshService mesh)
{
    /// <summary>The mesh service this singleton was built with.</summary>
    public IMeshService Mesh { get; } = mesh;
}

/// <summary>
/// MeshWeaver#7218 — <b>building the mesh service must construct no module's query provider.</b> A
/// module registers an <see cref="IMeshQueryProvider"/> singleton whose construction asks for a root
/// singleton that takes <see cref="IMeshService"/>. Building that <see cref="IMeshService"/> built the
/// query fan-out, and the fan-out LEASED every module provider to read its name — including the one whose
/// construction was asking, on the same thread. The container refused the re-entry as a circular
/// dependency on <see cref="IMeshQueryProvider"/>, and the registry reported the registration as not
/// constructible, on every pod, once per process.
///
/// <para>The reading: the first lease of the module's provider builds it (and through it a fresh
/// <see cref="IMeshService"/>), and afterwards no fault has been reported against that registration —
/// <see cref="ModuleServices.MarkReported"/> still accepts the circular-dependency cause as new. The
/// <see cref="IMeshService"/> the consumer was built with serves the module's provider. Negative control:
/// on the fan-out that leased at build time, the cause was already reported.</para>
/// </summary>
public sealed class AModuleProviderNeedingTheMeshServiceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.MeshServiceProvider";
    private const string AnsweredPath = "MeshServiceProviderChats/answer";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "mesh-service-provider-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<MeshServiceConsumer>())
            .InstallAssemblies(Write());

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    [Fact(Timeout = 180_000)]
    public async Task BuildingTheProvider_DoesNotLeaseItAgain_AndItServesTheMeshServiceItBuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        var generation = Contexts.Current(Module)
                         ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");
        var index = generation.Services!.Registrations
            .First(r => r.Descriptor.ServiceType == typeof(IMeshQueryProvider)).Index;

        var lease = Contexts.TryLeaseModuleService(Module, index, null, typeof(IMeshQueryProvider));
        lease.Should().NotBeNull("the provider is constructible");
        lease!.Value.Release.Dispose();

        generation.Services.MarkReported(index, typeof(InvalidOperationException).FullName!)
            .Should().BeTrue("constructing the provider never leased it again on the same thread — no circular "
                             + "dependency was reported against the registration");
        generation.Services.Unservable.Should().BeEmpty();

        var consumer = Mesh.ServiceProvider.GetRequiredService<MeshServiceConsumer>();
        await consumer.Mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{AnsweredPath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == AnsweredPath))
            .Should().Within(Budget)
            .Emit("the mesh service built during the provider's construction serves that provider", cancellationToken: ct);
    }

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Reactive.Linq;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Services;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.MeshServiceProvider.Module]
        namespace MeshWeaver.Test.MeshServiceProvider;
        public sealed class ChatsProvider(MeshWeaver.Graph.Test.MeshServiceConsumer directory) : IMeshQueryProvider
        {
            private static readonly MeshNode Answer =
                new("answer", "MeshServiceProviderChats") { Name = "answered by the module", NodeType = "Markdown" };
            public MeshWeaver.Graph.Test.MeshServiceConsumer Directory => directory;
            public string Name => "MeshWeaver.Test.MeshServiceProvider.ChatsProvider";
            public bool Matches(IReadOnlyList<string> queryNamespaces) => true;
            public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = (request.Query ?? "").Contains("MeshServiceProviderChats") && Answer is T answer ? [answer] : [],
                });
            public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                Observable.Return<IReadOnlyCollection<QueryResult>>([]);
            public IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                Observable.Return<T?>(default);
        }
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("MeshServiceProviderProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        // As the mail module registers its Teams chats provider: a factory whose dependency
                        // graph reaches a root singleton built over IMeshService.
                        services.AddSingleton<IMeshQueryProvider>(sp =>
                            new ChatsProvider(sp.GetRequiredService<MeshWeaver.Graph.Test.MeshServiceConsumer>()));
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(MeshServiceConsumer).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
