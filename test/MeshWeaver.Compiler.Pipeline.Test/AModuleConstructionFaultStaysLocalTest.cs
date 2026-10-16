using System.Reactive.Linq;
using Autofac;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Review of MeshWeaver#6854 — a module's forwarded query provider registered TRANSIENT, whose
/// construction the test can make fail on demand. Two properties of the lease (MeshWeaver#6847):
///
/// <list type="number">
///   <item><b>One construction per query subscription.</b> The fan-out used to take a lease to decide
///   membership, release it, and resolve the provider AGAIN when the leg subscribed — two constructions
///   of a transient, and a second one that failed erred the whole merged query. Membership and
///   execution now share ONE lease taken on subscribe.</item>
///   <item><b>A construction fault is local to its lease.</b> A fault in one caller's scope used to
///   record the registration UNSERVABLE for every caller of the generation. Only a generation-wide fault
///   (the registration is not laid out the way the root forwards it) is a durable verdict now; a
///   failing scope followed by a healthy one serves the healthy one. The caller's own scope ending
///   still propagates.</item>
/// </list>
/// </summary>
public sealed class AModuleConstructionFaultStaysLocalTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.TransientProvider";
    private const string ProviderType = Module + ".TransientChatsProvider";
    private const string ChatsPath = "TransientProviderChats/answer";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "transient-provider-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write());

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration Held() =>
        Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    private Type Provider() =>
        Held().Assembly.GetType(ProviderType) ?? throw new Xunit.Sdk.XunitException($"{ProviderType} is not in the module");

    private int Constructions => (int)Provider().GetField("Constructions")!.GetValue(null)!;

    // The construction with this number (counted from the module's load) throws; 0 means none does.
    private void FailConstruction(int number) => Provider().GetField("FailAt")!.SetValue(null, number);

    // The forwarded provider's registration index in the held generation.
    private int ProviderIndex() =>
        Held().Services!.Registrations
            .Single(r => r.Descriptor.ServiceType == typeof(IMeshQueryProvider)).Index;

    [Fact(Timeout = 180_000)]
    public async Task AQuery_ConstructsATransientModuleProviderOnce_AndAnswersFromIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var core = Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        IObservable<QueryResultChange<MeshNode>> Chats() =>
            core.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem(), Mesh.JsonSerializerOptions);

        // Positive control: the healthy provider answers its own node.
        await Chats().Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the healthy transient provider answers its own node", cancellationToken: ct);

        // The SECOND construction from here fails: a query that constructs twice (probe, then leg) errs.
        var before = Constructions;
        FailConstruction(before + 2);
        await Chats().Where(change => change.ChangeType == QueryChangeType.Initial)
            .Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("one subscription constructs the transient provider ONCE and answers from that instance", cancellationToken: ct);
        Constructions.Should().Be(before + 1, "membership and execution share one lease — one construction");

        // The next query's construction is the failing one: that subscription goes without the module,
        // the query still answers, and nothing is recorded against the registration.
        await Chats().Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == ChatsPath))
            .Where(answeredByTheModule => !answeredByTheModule)
            .Should().Within(Budget)
            .Emit("a provider whose construction fails is not a member of that subscription — and does not err it", cancellationToken: ct);
        Held().Services!.Unservable.Should().BeEmpty("a construction fault is that lease's alone, never a generation-wide verdict");

        // And the one after that has the module again.
        await Chats().Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the next subscription resolves the provider afresh and the module answers again", cancellationToken: ct);
    }

    [Fact]
    public void AFailingScope_FollowedByAHealthyScope_ServesTheHealthyScope()
    {
        var index = ProviderIndex();
        var lifetime = Mesh.ServiceProvider.GetRequiredService<ILifetimeScope>();
        using var failing = lifetime.BeginLifetimeScope();
        using var healthy = lifetime.BeginLifetimeScope();

        FailConstruction(Constructions + 1);
        var failed = Contexts.TryLeaseModuleService(Module, index, failing, typeof(IMeshQueryProvider));
        failed.Should().BeNull("the construction in the failing scope throws, so that lease has nothing to serve");

        var served = Contexts.TryLeaseModuleService(Module, index, healthy, typeof(IMeshQueryProvider));
        served.Should().NotBeNull("one caller's construction fault never marks the registration unservable for the next caller");
        served!.Value.Instance.Should().BeAssignableTo<IMeshQueryProvider>();
        served.Value.Release.Dispose();
        Held().Services!.Unservable.Should().BeEmpty("nothing about this generation was recorded");

        // The caller's own scope ending is the caller's fault and propagates — never "absent".
        var ended = lifetime.BeginLifetimeScope();
        ended.Dispose();
        var disposal = Record.Exception(() => Contexts.TryLeaseModuleService(Module, index, ended, typeof(IMeshQueryProvider)));
        disposal.Should().NotBeNull("a lease asked for by a caller whose scope has ended is that caller's fault");
        Causes(disposal!).Should().Contain(cause => cause is ObjectDisposedException,
            "the scope's disposal is what the caller sees, whatever the container wrapped it in");
        Held().Services!.Unservable.Should().BeEmpty("a caller's ended scope says nothing about the generation");
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        for (var cause = exception; cause is not null; cause = cause.InnerException)
            yield return cause;
    }

    private const string Source = $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.TransientProvider.Module]
        namespace MeshWeaver.Test.TransientProvider;
        public sealed class TransientChatsProvider : MeshWeaver.Mesh.Services.IMeshQueryProvider
        {
            public static int Constructions;
            public static int FailAt;
            private static readonly MeshWeaver.Mesh.MeshNode Answer =
                new("answer", "TransientProviderChats") { Name = "answered by the module", NodeType = "Markdown" };
            public TransientChatsProvider()
            {
                var number = System.Threading.Interlocked.Increment(ref Constructions);
                if (number == FailAt)
                    throw new System.InvalidOperationException($"construction #{number} refused by the test");
            }
            public string Name => "{{ProviderType}}";
            public bool Matches(System.Collections.Generic.IReadOnlyList<string> queryNamespaces) => true;
            public System.IObservable<MeshWeaver.Mesh.QueryResultChange<T>> Query<T>(
                MeshWeaver.Mesh.Services.MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return(new MeshWeaver.Mesh.QueryResultChange<T>
                {
                    ChangeType = MeshWeaver.Mesh.QueryChangeType.Initial,
                    Items = (request.Query ?? "").Contains("TransientProviderChats") && Answer is T answer ? [answer] : [],
                });
            public System.IObservable<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                MeshWeaver.Mesh.Services.AutocompleteMode mode = MeshWeaver.Mesh.Services.AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                System.Reactive.Linq.Observable.Return<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>>([]);
            public System.IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return<T?>(default);
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("TransientProviderProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddTransient<MeshWeaver.Mesh.Services.IMeshQueryProvider, TransientChatsProvider>();
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(Source)], PlatformReferences.Platform(),
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
