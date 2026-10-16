using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A PLATFORM options type an optional module configures for a name of its own — the shape of a
/// module's <c>AddHttpClient(name)</c>, which configures <c>HttpClientFactoryOptions</c> by name.</summary>
public sealed class SignInProbeOptions
{
    /// <summary>Who configured this name.</summary>
    public string ConfiguredBy { get; set; } = "platform";
}

/// <summary>
/// 🚨 MeshWeaver#6847 — <b>an optional module that cannot serve one of its forwarded services never
/// breaks sign-in.</b> On memex, 2026-10-16, WhatsApp went live with a generation whose forwarded
/// registrations sat at other indexes than the root's forwarders were laid out for at boot. Its query
/// provider's forwarder resolved <c>mw-module-service:10 (IWhatsAppRecentChats)</c>, which was not
/// registered — and because the query fan-out leases EVERY module's provider for EVERY query, node
/// resolution faulted for every hub activation, and the sign-in's identity directory query
/// (<c>nodeType:User</c>, the one <c>UserIdentityCache</c> holds) faulted with it: nobody could sign in.
///
/// <para>The module here is that incident in miniature: a query provider and a NAMED setup of a platform
/// options type, forwarded to the root. The test then makes the held module's current generation the
/// broken one — a module-owned registration inserted ahead of both, exactly as WhatsApp 1.4.2 did — by
/// committing it past the shape check the live swap now applies (#6847's first half). It then runs what
/// sign-in runs: the identity directory query, a node write (the persisted authorization code), a
/// hub-activating read of that node, and a named options build of the type the module also configures
/// (an OIDC handler's backchannel client).</para>
///
/// <para><b>Controls.</b> Before the break, the module's provider answers its own node and its setup
/// configures its own name — so its absence afterwards is the module's feature being left out, not a
/// provider never asked. After the break, a DIRECT resolve of the broken registration still throws
/// "has not been registered" — so the module really is broken, and sign-in surviving is the isolation,
/// not a repair. Mutation-checked: with <c>ModuleContexts.TryLeaseModuleService</c> rethrowing the
/// resolve fault, the identity query and the read fault exactly as on memex.</para>
/// </summary>
public sealed class ABrokenOptionalModuleNeverBreaksSignInTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.SignInIsolation";
    private const string ChatsPath = "SignInIsolationChats/answer";
    private const string ModuleClient = "module-client";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "signin-isolation-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write("g1", Source(broken: false)));

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    private ModuleGeneration Held() =>
        Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    [Fact(Timeout = 180_000)]
    public async Task SignInSucceeds_WhileAnOptionalModuleCannotServeItsForwardedServices()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Mesh.ServiceProvider.GetRequiredService<IOptionsMonitor<SignInProbeOptions>>();

        // Positive control: the healthy module is a member of the fan-out and configures its own name.
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == ChatsPath))
            .Should().Within(Budget)
            .Emit("the healthy module's provider answers its own node", cancellationToken: ct);
        options.Get(ModuleClient).ConfiguredBy.Should().Be("module", "the healthy module configures its own name");

        BreakTheHeldModule();

        // The module really is broken: its root forwarder's registration no longer resolves.
        var direct = Record.Exception(() => Contexts.ResolveModuleService(Module, 0));
        direct.Should().NotBeNull("the broken generation cannot produce the registration the root forwards");
        direct!.ToString().Should().Contain("has not been registered");

        // 1. The identity check — UserIdentityCache's directory query.
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:User").AsSystem())
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Should().Within(Budget)
            .Emit("the identity directory query answers while an optional module is broken", cancellationToken: ct);

        // 2. The authorization code persists, and 3. a hub activates to read it back.
        var code = new MeshNode("code-" + Guid.NewGuid().ToString("N")[..8], TestPartition)
        {
            Name = "authorization code",
            NodeType = "Markdown",
        };
        await MeshQuery.CreateNode(code).Should().Within(Budget)
            .Emit("a node write — the persisted code — succeeds while an optional module is broken", cancellationToken: ct);
        await ReadNode(code.Path).Where(node => node?.Path == code.Path)
            .Should().Within(Budget)
            .Emit("a hub activates and resolves its node while an optional module is broken", cancellationToken: ct);

        // 4. A named options build of the type the module also configures — the OIDC backchannel shape.
        var oidc = Record.Exception(() => options.Get("oidc-backchannel"));
        oidc.Should().BeNull("another name's options build never runs into the broken module's setup");

        // The module's feature is ABSENT — named, never silent.
        var unservable = Held().Services!.Unservable;
        unservable.Should().ContainKey(0, "the query provider's registration is recorded unservable in this generation");
        unservable.Should().ContainKey(1, "the options setup's registration is recorded unservable in this generation");
        unservable[0].Should().Contain(Module).And.Contain("has not been registered");
        unservable[1].Should().Contain("where the root's forwarder forwards");
        options.Get(ModuleClient).ConfiguredBy.Should().Be("platform", "the broken module configures nothing");
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem())
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == ChatsPath))
            .Where(answeredByTheModule => !answeredByTheModule)
            .Should().Within(Budget)
            .Emit("the broken module's provider has left the fan-out", cancellationToken: ct);
    }

    /// <summary>
    /// Review of #6854: a query built while the module was healthy and SUBSCRIBED after its generation
    /// changed. Membership used to be decided when the query was built and the leg resolved the provider
    /// again on subscribe, so the generation change in between erred the WHOLE merged identity query with
    /// a <see cref="ModuleRetiredException"/>. Membership and execution now share one lease taken on
    /// subscribe, so the broken module is simply not a member of that subscription.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AQueryBuiltBeforeAGenerationChange_AnswersWithoutTheModule_WhenSubscribedAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var core = Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var identity = core.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:User").AsSystem(), Mesh.JsonSerializerOptions);
        var chats = core.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{ChatsPath}").AsSystem(), Mesh.JsonSerializerOptions);

        BreakTheHeldModule();

        await identity
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Should().Within(Budget)
            .Emit("the identity query built before the generation change answers when subscribed after it", cancellationToken: ct);
        await chats
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == ChatsPath))
            .Where(answeredByTheModule => !answeredByTheModule)
            .Should().Within(Budget)
            .Emit("the module that can no longer be leased is not a member of the subscription", cancellationToken: ct);
    }

    // Commits, past the live swap's shape check, a generation whose forwarded registrations moved: a
    // module-owned registration inserted ahead of them, as WhatsApp 1.4.2 did. The root's forwarders were
    // laid out at boot for index 0 (the query provider) and 1 (the options setup).
    private void BreakTheHeldModule()
    {
        var held = Held();
        var stage = new ModuleSwapStage();
        var broken = Contexts.LoadStaged(Write("g2", Source(broken: true)), stage);
        var contributions = ModuleContributions.Of(broken.Assembly);
        Contexts.SetContributions(broken, contributions);
        var services = ModuleServices.Probe(broken.Name, broken.Context, contributions.ServiceConfigurations, held.Services!.Prefix);
        services.Stage = broken.Context.Stage;
        broken.Services = services;
        Contexts.BuildBeforePublish(broken).Should().BeNull("the broken generation's own container builds — it is the root's view of it that is wrong");
        stage.Add(broken);
        Contexts.CommitAll(stage);
        Held().Should().BeSameAs(broken);
    }

    private static string Source(bool broken) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.SignInIsolation.Module]
        namespace MeshWeaver.Test.SignInIsolation;
        public sealed class RecentChats { }
        public sealed class ChatsProvider : MeshWeaver.Mesh.Services.IMeshQueryProvider
        {
            private static readonly MeshWeaver.Mesh.MeshNode Answer =
                new("answer", "SignInIsolationChats") { Name = "answered by the module", NodeType = "Markdown" };
            public string Name => "MeshWeaver.Test.SignInIsolation.ChatsProvider";
            public bool Matches(System.Collections.Generic.IReadOnlyList<string> queryNamespaces) => true;
            public System.IObservable<MeshWeaver.Mesh.QueryResultChange<T>> Query<T>(
                MeshWeaver.Mesh.Services.MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return(new MeshWeaver.Mesh.QueryResultChange<T>
                {
                    ChangeType = MeshWeaver.Mesh.QueryChangeType.Initial,
                    Items = (request.Query ?? "").Contains("SignInIsolationChats") && Answer is T answer ? [answer] : [],
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
                [new MeshWeaver.Mesh.MeshNode("SignInIsolationProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        {{(broken ? "services.AddSingleton<RecentChats>();" : "")}}
                        services.AddSingleton<MeshWeaver.Mesh.Services.IMeshQueryProvider, ChatsProvider>();
                        services.Configure<MeshWeaver.Graph.Test.SignInProbeOptions>("{{ModuleClient}}", o => o.ConfiguredBy = "module");
                        return services;
                    })];
        }
        """;

    private string Write(string generation, string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(SignInProbeOptions).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
