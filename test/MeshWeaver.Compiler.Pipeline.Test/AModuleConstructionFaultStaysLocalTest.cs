using System.Reactive.Linq;
using Autofac;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Review of MeshWeaver#6854 — a module whose forwarded services are registered TRANSIENT, so every
/// lease constructs them, and whose constructions the test can make fail on demand: a query provider, a
/// node-type access rule, and a named setup of a platform options type. What the lease
/// (MeshWeaver#6847) must hold to:
///
/// <list type="number">
///   <item><b>One construction per query subscription</b>, and that lease held only as long as the
///   module's own leg runs: a completed module leg never keeps a retirement waiting while the merged
///   query stays live on its storage legs.</item>
///   <item><b>A construction fault is local to its lease.</b> Only a generation-wide fault is a durable
///   verdict; a failing scope followed by a healthy one serves the healthy one; a constructor that runs
///   into a disposed dependency of its OWN under a live caller is isolated like any other fault, while a
///   caller whose own scope has ended still sees its disposal.</item>
///   <item><b>A failed INITIAL binding drops nothing for good.</b> The query fan-out and the access-rule
///   set are built once; a module service whose lease failed then is retained and serves once it can.</item>
///   <item><b>A temporary fault in an options setup is never cached as complete options</b>, and never
///   fails the build of any name.</item>
/// </list>
/// </summary>
public sealed class AModuleConstructionFaultStaysLocalTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.TransientProvider";
    private const string ProviderType = Module + ".TransientChatsProvider";
    private const string RuleType = Module + ".TransientRule";
    private const string SetupType = Module + ".TransientSetup";
    private const string ChatsPath = "TransientProviderChats/answer";
    private const string GovernedNodeType = "TransientRuleGoverned";
    private const string ModuleClient = "transient-module-client";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "transient-provider-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write());

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration Held() =>
        Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    private Type ModuleType(string name) =>
        Held().Assembly.GetType(name) ?? throw new Xunit.Sdk.XunitException($"{name} is not in the module");

    private int Constructions(string type) => (int)ModuleType(type).GetField("Constructions")!.GetValue(null)!;

    // The construction of `type` with this number (counted from the module's load) throws; 0 means none.
    private void FailConstruction(string type, int number) => ModuleType(type).GetField("FailAt")!.SetValue(null, number);

    // Whether the failing construction throws an ObjectDisposedException — a disposed dependency of its own.
    private void FailWithOwnDisposal(string type) => ModuleType(type).GetField("FailDisposed")!.SetValue(null, true);

    private int Index<TService>() =>
        Held().Services!.Registrations.First(r => r.Descriptor.ServiceType == typeof(TService)).Index;

    private IObservable<QueryResultChange<MeshNode>> CoreQuery(string query) =>
        Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query).AsSystem(), Mesh.JsonSerializerOptions);

    [Fact(Timeout = 180_000)]
    public async Task AQuery_ConstructsATransientModuleProviderOnce_AndAnswersFromIt()
    {
        var ct = TestContext.Current.CancellationToken;

        // Positive control: the healthy provider answers its own node.
        await CoreQuery($"path:{ChatsPath}").Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the healthy transient provider answers its own node", cancellationToken: ct);

        // The SECOND construction from here fails: a query that constructs twice (probe, then leg) errs.
        var before = Constructions(ProviderType);
        FailConstruction(ProviderType, before + 2);
        await CoreQuery($"path:{ChatsPath}").Where(change => change.ChangeType == QueryChangeType.Initial)
            .Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("one subscription constructs the transient provider ONCE and answers from that instance", cancellationToken: ct);
        Constructions(ProviderType).Should().Be(before + 1, "membership and execution share one lease — one construction");

        // The next query's construction is the failing one: that subscription goes without the module,
        // the query still answers, and nothing is recorded against the registration.
        await CoreQuery($"path:{ChatsPath}").Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == ChatsPath))
            .Where(answeredByTheModule => !answeredByTheModule)
            .Should().Within(Budget)
            .Emit("a provider whose construction fails is not a member of that subscription — and does not err it", cancellationToken: ct);
        Held().Services!.Unservable.Should().BeEmpty("a construction fault is that lease's alone, never a generation-wide verdict");

        // And the one after that has the module again.
        await CoreQuery($"path:{ChatsPath}").Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the next subscription resolves the provider afresh and the module answers again", cancellationToken: ct);
    }

    /// <summary>
    /// The module's leg completes at once (its provider answers one Initial and completes) while the
    /// merged query stays live on its storage leg. The module leg's lease must end with that leg, so a
    /// retirement of the module unloads it while the query is still subscribed.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ACompletedModuleLeg_ReleasesItsLease_WhileTheMergedQueryStaysLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = new System.Reactive.Subjects.AsyncSubject<bool>();
        using var live = CoreQuery("nodeType:User")
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Subscribe(_ => { initial.OnNext(true); initial.OnCompleted(); }, initial.OnError);
        await initial.Should().Within(Budget)
            .Emit("the identity directory query answers, its module leg already complete", cancellationToken: ct);

        await Contexts.Retire(Held(), TimeSpan.FromSeconds(10))
            .Where(unloaded => unloaded)
            .Should().Within(Budget)
            .Emit("the completed module leg holds no lease, so the retirement unloads while the merged query is still live",
                cancellationToken: ct);
    }

    [Fact]
    public void AFailingScope_FollowedByAHealthyScope_ServesTheHealthyScope()
    {
        var index = Index<IMeshQueryProvider>();
        var lifetime = Mesh.ServiceProvider.GetRequiredService<ILifetimeScope>();
        using var failing = lifetime.BeginLifetimeScope();
        using var healthy = lifetime.BeginLifetimeScope();

        FailConstruction(ProviderType, Constructions(ProviderType) + 1);
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

    /// <summary>
    /// A constructor that runs into a disposed dependency of its OWN while the caller is alive is a
    /// construction fault — isolated to that lease — and never the caller's disposal rethrown into a
    /// whole query.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AConstructorsOwnDisposedDependency_UnderALiveCaller_IsIsolated()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = Index<IMeshQueryProvider>();
        using var healthy = Mesh.ServiceProvider.GetRequiredService<ILifetimeScope>().BeginLifetimeScope();
        FailWithOwnDisposal(ProviderType);

        FailConstruction(ProviderType, Constructions(ProviderType) + 1);
        var lease = Record.Exception(() => Contexts.TryLeaseModuleService(Module, index, healthy, typeof(IMeshQueryProvider)));
        lease.Should().BeNull("the disposal is the constructor's own, not the live caller's");

        FailConstruction(ProviderType, Constructions(ProviderType) + 1);
        await CoreQuery("nodeType:User").Where(change => change.ChangeType == QueryChangeType.Initial)
            .Should().Within(Budget)
            .Emit("the identity directory query answers while the module provider's own dependency is disposed",
                cancellationToken: ct);
        Held().Services!.Unservable.Should().BeEmpty("a construction fault is never a generation-wide verdict");
    }

    /// <summary>
    /// The fan-out and the access-rule set are built once. A module service whose lease failed while they
    /// were built is retained, and serves on later use once its construction succeeds.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFailedInitialBinding_DropsNothingForGood()
    {
        var ct = TestContext.Current.CancellationToken;

        FailConstruction(ProviderType, Constructions(ProviderType) + 1);
        var query = new MeshWeaver.Hosting.Persistence.Query.MeshQuery(
            Mesh.ServiceProvider.GetServices<IMeshQueryProvider>(), Mesh);
        await query.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("a provider whose lease failed while the fan-out was built answers once it can be constructed",
                cancellationToken: ct);

        FailConstruction(RuleType, Constructions(RuleType) + 1);
        var rules = new NodeTypeAccessRuleSet(Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>());
        rules.Find(GovernedNodeType, NodeOperation.Read).Should().NotBeNull(
            "a rule whose lease failed while the set was built governs its type once it can be constructed");
        rules.Find(GovernedNodeType, NodeOperation.Read)!.NodeType.Should().Be(GovernedNodeType);
    }

    /// <summary>
    /// The module registers its provider TWICE — one logical provider, one name. The first registration's
    /// lease fails while the fan-out is built, so it is held under its forwarder's label while the second is
    /// bound under the real name; once the first can be leased, a subscription sees two members with the
    /// same name. The provider must still run ONCE per query (one change feed), the first registration
    /// winning, the duplicate's lease released at once.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADuplicateRegistration_WhoseFirstBindingFailed_RunsOncePerQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        FailConstruction(ProviderType, Constructions(ProviderType) + 1);
        var query = new MeshWeaver.Hosting.Persistence.Query.MeshQuery(
            Mesh.ServiceProvider.GetServices<IMeshQueryProvider>(), Mesh);

        var queried = Queries();
        await query.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the recovered provider answers", cancellationToken: ct);
        (Queries() - queried).Should().Be(1, "one logical provider is queried once per query, never once per registration");
    }

    private int Queries() => (int)ModuleType(ProviderType).GetField("Queries")!.GetValue(null)!;

    /// <summary>
    /// A temporary construction fault of a module's options setup costs THAT build the module's setup —
    /// the name still builds, as every other name does — but is never cached as complete options: the
    /// next build of the name carries the setup again.
    /// </summary>
    [Fact]
    public void ATemporarySetupFault_IsNeverCachedAsCompleteOptions()
    {
        var options = Mesh.ServiceProvider.GetRequiredService<IOptionsMonitor<SignInProbeOptions>>();

        FailConstruction(SetupType, Constructions(SetupType) + 1);
        var during = Record.Exception(() => options.Get(ModuleClient).ConfiguredBy.Should().Be("platform",
            "the module's setup could not be constructed for this build, so this build goes without it"));
        during.Should().BeNull("a temporary setup fault never fails the options build");

        options.Get(ModuleClient).ConfiguredBy.Should().Be("module",
            "the build made over the temporary fault was not cached, so the next one carries the module's setup");
        Held().Services!.Unservable.Should().BeEmpty("a temporary setup fault is never a generation-wide verdict");
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        for (var cause = exception; cause is not null; cause = cause.InnerException)
            yield return cause;
    }

    private const string Fault = """
            public static int Constructions;
            public static int FailAt;
            public static bool FailDisposed;
            internal static void Construct()
            {
                var number = System.Threading.Interlocked.Increment(ref Constructions);
                if (number != FailAt)
                    return;
                if (FailDisposed)
                    throw new System.ObjectDisposedException("a dependency the constructor owns");
                throw new System.InvalidOperationException($"construction #{number} refused by the test");
            }
        """;

    private const string Source = $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.TransientProvider.Module]
        namespace MeshWeaver.Test.TransientProvider;
        public sealed class TransientChatsProvider : MeshWeaver.Mesh.Services.IMeshQueryProvider
        {
        {{Fault}}
            private static readonly MeshWeaver.Mesh.MeshNode Answer =
                new("answer", "TransientProviderChats") { Name = "answered by the module", NodeType = "Markdown" };
            public static int Queries;
            public TransientChatsProvider() => Construct();
            public string Name => "{{ProviderType}}";
            public bool Matches(System.Collections.Generic.IReadOnlyList<string> queryNamespaces) => true;
            public System.IObservable<MeshWeaver.Mesh.QueryResultChange<T>> Query<T>(
                MeshWeaver.Mesh.Services.MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Defer(() => { System.Threading.Interlocked.Increment(ref Queries); return System.Reactive.Linq.Observable.Return(new MeshWeaver.Mesh.QueryResultChange<T>
                {
                    ChangeType = MeshWeaver.Mesh.QueryChangeType.Initial,
                    Items = (request.Query ?? "").Contains("TransientProviderChats") && Answer is T answer ? [answer] : [],
                }); });
            public System.IObservable<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                MeshWeaver.Mesh.Services.AutocompleteMode mode = MeshWeaver.Mesh.Services.AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                System.Reactive.Linq.Observable.Return<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>>([]);
            public System.IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return<T?>(default);
        }
        public sealed class TransientRule : MeshWeaver.Mesh.Services.INodeTypeAccessRule
        {
        {{Fault}}
            public TransientRule() => Construct();
            public string NodeType => "{{GovernedNodeType}}";
            public System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.Security.NodeOperation> SupportedOperations => [];
            public System.IObservable<bool> HasAccess(MeshWeaver.Mesh.Services.NodeValidationContext context, string? userId) =>
                System.Reactive.Linq.Observable.Return(true);
        }
        public sealed class TransientSetup : Microsoft.Extensions.Options.IConfigureNamedOptions<MeshWeaver.Graph.Test.SignInProbeOptions>
        {
        {{Fault}}
            public TransientSetup() => Construct();
            public void Configure(string? name, MeshWeaver.Graph.Test.SignInProbeOptions options)
            {
                if (name == "{{ModuleClient}}")
                    options.ConfiguredBy = "module";
            }
            public void Configure(MeshWeaver.Graph.Test.SignInProbeOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("TransientProviderProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddTransient<MeshWeaver.Mesh.Services.IMeshQueryProvider, TransientChatsProvider>();
                        // The same logical provider registered again — one name, two registrations.
                        services.AddTransient<MeshWeaver.Mesh.Services.IMeshQueryProvider>(_ => new TransientChatsProvider());
                        services.AddTransient<MeshWeaver.Mesh.Services.INodeTypeAccessRule, TransientRule>();
                        // A factory: a type registration would be refused, since the setup also implements
                        // IConfigureNamedOptions, which a forwarding proxy hides — the options forwarder serves it.
                        services.AddTransient<Microsoft.Extensions.Options.IConfigureOptions<MeshWeaver.Graph.Test.SignInProbeOptions>>(
                            _ => new TransientSetup());
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(SignInProbeOptions).Assembly.Location));
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
