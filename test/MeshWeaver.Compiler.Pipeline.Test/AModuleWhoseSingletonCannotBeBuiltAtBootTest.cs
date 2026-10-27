using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A PLATFORM interface the boot-built test module implements.</summary>
public interface IBootBuiltProbe
{
    /// <summary>Says which module answered.</summary>
    string Answer();
}

/// <summary>
/// A stand-in for CIRCUIT-scoped platform state — Blazor's <c>NavigationManager</c>, which a Blazor
/// circuit initialises and nothing else does. Registered SCOPED in the root; using one that no scope
/// initialised throws exactly as <c>RemoteNavigationManager</c> does (MeshWeaver#6556).
/// </summary>
public sealed class CircuitBoundState
{
    private int initialized;

    /// <summary>What the circuit does when it starts.</summary>
    public void Initialize() => Volatile.Write(ref initialized, 1);

    /// <summary>What a consumer's constructor does — a component library's <c>DialogService</c> subscribes to
    /// <c>NavigationManager.LocationChanged</c>.</summary>
    public void Subscribe()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("'CircuitBoundState' has not been initialized.");
    }

    private int released;

    /// <summary>How many consumers built over this state were disposed — what a circuit's end does to
    /// the services it owns.</summary>
    public int Released => Volatile.Read(ref released);

    /// <summary>What a consumer's <c>Dispose</c> does — a component library's <c>DialogService</c> unsubscribes.</summary>
    public void Release() => Interlocked.Increment(ref released);
}

/// <summary>A platform CLASS a module registers SCOPED over circuit state — the route a component-library service
/// takes when its assembly is in the image (<c>Current</c>).</summary>
public sealed class CircuitPlatformService
{
    /// <summary>Takes the circuit's state, as <c>DialogService</c> takes a <c>NavigationManager</c>.</summary>
    public CircuitPlatformService(CircuitBoundState state)
    {
        state.Subscribe();
        State = state;
    }

    /// <summary>The circuit state it was built over.</summary>
    public CircuitBoundState State { get; }
}

/// <summary>
/// MeshWeaver#6542 — <b>built before published, at boot.</b> Every process-lifetime instance the root
/// can reach in a module's container is built once the root container exists and before the root
/// provider is handed to anyone. A module whose such singleton cannot be built is WITHDRAWN — never left
/// half-published — and reported as an <see cref="IncompatibleModule"/>, the record <c>/health</c> and
/// the module-status surfaces read; the mesh boots without it. MeshWeaver#6556: what belongs to a
/// circuit is never built at boot, so it can never withdraw the module.
/// </summary>
public abstract class ModuleSingletonsBuiltAtBootTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The module's simple name.</summary>
    protected const string Module = "MeshWeaver.Test.BootBuilt";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-built-" + Guid.NewGuid().ToString("N"));

    /// <summary>Whether the module carries a singleton whose constructor throws.</summary>
    protected abstract bool Broken { get; }

    /// <summary>Whether the module carries circuit-bound registrations (MeshWeaver#6556).</summary>
    protected virtual bool CircuitBound => false;

    /// <summary>Whether the module registers a further <see cref="InstalledModuleAssembly"/> of its own —
    /// the reference assembly it ships, as MeshWeaver.Testing registers its xunit closure (MeshWeaver#6557).</summary>
    protected virtual bool ShipsReferences => false;

    /// <summary>Whether the module registers an <see cref="MeshWeaver.Mesh.Services.INodeTypeAccessRule"/> —
    /// a security rule, which the root forwards as a platform interface.</summary>
    protected virtual bool RegistersAccessRule => false;

    /// <summary>Whether the module registers an <see cref="MeshWeaver.Mesh.Services.IMeshQueryProvider"/> —
    /// a query source, which the root forwards as a platform interface (MeshWeaver#6793).</summary>
    protected virtual bool RegistersQueryProvider => false;

    /// <summary>The path of the node the module's query provider answers with.</summary>
    public const string QueriedProbePath = "BootBuiltQueried/answer";

    /// <summary>The node type the module's access rule governs.</summary>
    public const string GuardedNodeType = "BootBuiltGuarded";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
            .InstallAssemblies(WriteModule(root, Broken, CircuitBound, ShipsReferences, RegistersAccessRule, RegistersQueryProvider));

    /// <summary>The mesh's module registry.</summary>
    protected ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    /// <summary>The records of modules this replica carries and does not run.</summary>
    protected IncompatibleModule[] Incompatible =>
        Mesh.ServiceProvider.GetServices<IncompatibleModule>().Where(m => m.Name == Module).ToArray();

    /// <summary>The installed modules of this name, as the compile references, the fingerprint and the
    /// pre-warm sweep read them.</summary>
    protected InstalledModuleAssembly[] Installed =>
        Mesh.ServiceProvider.GetServices<InstalledModuleAssembly>()
            .Where(m => m.Assembly.GetName().Name == Module)
            .ToArray();

    /// <summary>The module's simple name, for tests that build their own host.</summary>
    public const string ModuleName = Module;

    /// <summary>The simple name of the reference assembly the module ships when
    /// <see cref="ShipsReferences"/> — one no image-bound module of the test mesh installs.</summary>
    public const string ShippedReferenceName = "System.Private.Xml";

    private static string Source(bool broken, bool circuitBound, bool shipsReferences, bool accessRule, bool queryProvider) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.BootBuilt.Module]
        namespace MeshWeaver.Test.BootBuilt;
        public sealed class Probe : MeshWeaver.Graph.Test.IBootBuiltProbe { public string Answer() => "boot-built"; }
        public sealed class GuardRule : MeshWeaver.Mesh.Services.INodeTypeAccessRule
        {
            public string NodeType => "{{GuardedNodeType}}";
            public System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.Security.NodeOperation> SupportedOperations { get; } = [];
            public System.IObservable<bool> HasAccess(MeshWeaver.Mesh.Services.NodeValidationContext context, string? userId) =>
                System.Reactive.Linq.Observable.Return(true);
        }
        public sealed class QueryProbe : MeshWeaver.Mesh.Services.IMeshQueryProvider
        {
            private static readonly MeshWeaver.Mesh.MeshNode Answer =
                new("answer", "BootBuiltQueried") { Name = "answered by the module", NodeType = "Markdown" };
            public string Name => "MeshWeaver.Test.BootBuilt.QueryProbe";
            public bool Matches(System.Collections.Generic.IReadOnlyList<string> queryNamespaces) => true;
            public System.IObservable<MeshWeaver.Mesh.QueryResultChange<T>> Query<T>(
                MeshWeaver.Mesh.Services.MeshQueryRequest request, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return(new MeshWeaver.Mesh.QueryResultChange<T>
                {
                    ChangeType = MeshWeaver.Mesh.QueryChangeType.Initial,
                    Items = request.Query.Contains("BootBuiltQueried") && Answer is T answer ? [answer] : [],
                });
            public System.IObservable<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>> Autocomplete(
                string basePath, string prefix, System.Text.Json.JsonSerializerOptions options,
                MeshWeaver.Mesh.Services.AutocompleteMode mode = MeshWeaver.Mesh.Services.AutocompleteMode.RelevanceFirst,
                int limit = 10, string? contextPath = null, string? context = null) =>
                System.Reactive.Linq.Observable.Return<System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.QueryResult>>([]);
            public System.IObservable<T?> Select<T>(string path, string property, System.Text.Json.JsonSerializerOptions options) =>
                System.Reactive.Linq.Observable.Return<T?>(default);
        }
        public sealed class Fragile
        {
            public Fragile() { {{(broken ? "throw new System.InvalidOperationException(\"cannot be built by design\");" : "")}} }
        }
        public sealed class CircuitDialog : System.IDisposable
        {
            public CircuitDialog(MeshWeaver.Graph.Test.CircuitBoundState state) { state.Subscribe(); State = state; }
            public MeshWeaver.Graph.Test.CircuitBoundState State { get; }
            public void Dispose() => State.Release();
        }
        public sealed class CircuitBoundSingleton
        {
            public CircuitBoundSingleton(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe();
        }
        public sealed class TakesACircuitBoundSingleton
        {
            public TakesACircuitBoundSingleton(CircuitBoundSingleton inner) { }
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("BootBuiltProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<MeshWeaver.Graph.Test.IBootBuiltProbe, Probe>();
                        services.AddSingleton<Fragile>();
                        {{(broken ? ContributedInstalledModule : "")}}
                        {{(circuitBound ? CircuitBoundRegistrations : "")}}
                        {{(shipsReferences ? ShippedReferenceRegistration : "")}}
                        {{(accessRule ? AccessRuleRegistration : "")}}
                        {{(queryProvider ? QueryProviderRegistration : "")}}
                        return services;
                    })];
        }
        """;

    // A module that registers an installed module through its OWN services — a further reference
    // assembly (MeshWeaver#6557): the root forwards it, and once the module is withdrawn that member
    // must be absent from the set, never a forwarder that can only throw.
    private const string ContributedInstalledModule =
        "services.AddSingleton(new MeshWeaver.Mesh.InstalledModuleAssembly(typeof(Probe).Assembly));";

    // The circuit-scoped shape: scoped services over circuit state (a module class and a platform class), a
    // singleton that takes circuit state, and a singleton that takes that singleton.
    private const string CircuitBoundRegistrations = """
        services.AddScoped<CircuitDialog>();
                                services.AddScoped<MeshWeaver.Graph.Test.CircuitPlatformService>();
                                services.AddSingleton<CircuitBoundSingleton>();
                                services.AddSingleton<TakesACircuitBoundSingleton>();
        """;

    // The MeshWeaver.Testing shape: the module registers an assembly it ships as an installed module.
    private const string ShippedReferenceRegistration = """
        services.AddSingleton(new MeshWeaver.Mesh.InstalledModuleAssembly(typeof(System.Xml.XmlDocument).Assembly));
        """;

    // A security rule the module contributes — forwarded to the root as a platform interface.
    private const string AccessRuleRegistration =
        "services.AddSingleton<MeshWeaver.Mesh.Services.INodeTypeAccessRule, GuardRule>();";

    // A query source the module contributes — forwarded to the root as a platform interface (MeshWeaver#6793).
    private const string QueryProviderRegistration =
        "services.AddSingleton<MeshWeaver.Mesh.Services.IMeshQueryProvider, QueryProbe>();";

    /// <summary>Compiles the test module under <paramref name="directoryRoot"/> and returns its entry DLL —
    /// for hosts this base does not build (MeshWeaver#6557's plain MS DI host).</summary>
    internal static string WriteModule(string directoryRoot, bool broken, bool circuitBound, bool shipsReferences = false,
        bool accessRule = false, bool queryProvider = false) =>
        Write(directoryRoot, Source(broken, circuitBound, shipsReferences, accessRule, queryProvider));

    private static string Write(string directoryRoot, string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IBootBuiltProbe).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(directoryRoot, "modules", $"{Module}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}

/// <summary>A module whose singleton throws at boot is withdrawn and reported; the mesh serves without it.</summary>
public sealed class AModuleWhoseSingletonCannotBeBuiltAtBootTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    [Fact]
    public void TheModuleIsWithdrawnAndReported_AndTheMeshServesWithoutIt()
    {
        Contexts.Current(Module).Should().BeNull(
            "a module whose shared instances could not be built is never left published — half its singletons built");
        var record = Incompatible.Should().ContainSingle(
            "the withdrawal is reported where every module that failed to install is").Subject;
        record.Error.Should().Contain("could not be built before it was published")
            .And.Contain("cannot be built by design", "the constructor's own sentence is what an operator acts on");
        var probe = Mesh.ServiceProvider.GetRequiredService<IBootBuiltProbe>();
        var refused = Record.Exception(() => probe.Answer());
        refused.Should().BeOfType<InvalidOperationException>(
            "the root's forwarder has no generation to forward to — the feature is absent, said by name");
        refused!.Message.Should().Contain(Module);
    }
}

/// <summary>The negative control: the same module with a singleton that builds is published and serves.</summary>
public sealed class AModuleWhoseSingletonsBuildAtBootTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    [Fact]
    public void TheModuleIsPublished_AndServes()
    {
        Contexts.Current(Module).Should().NotBeNull();
        Incompatible.Should().BeEmpty();
        Installed.Should().ContainSingle("a published module is an installed module");
        Mesh.ServiceProvider.GetRequiredService<IBootBuiltProbe>().Answer().Should().Be("boot-built");
    }
}

/// <summary>
/// MeshWeaver#6556 — the circuit-scoped shape. A module registers services that belong to a CIRCUIT: scoped
/// ones over circuit state (a module class and a platform class), a singleton whose constructor takes
/// circuit state, and a singleton that takes THAT one. None is a process-lifetime instance the root
/// reaches, so none is built at boot — building one ran against state no circuit had initialised,
/// threw, and withdrew the module. The module is published and serves.
/// </summary>
public sealed class AModuleWithCircuitBoundServicesLoadsAtBootTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool CircuitBound => true;

    [Fact]
    public void TheModuleIsPublished_AndServes()
    {
        Incompatible.Should().BeEmpty("nothing that belongs to a circuit is built at boot, so nothing withdraws the module");
        Contexts.Current(Module).Should().NotBeNull();
        Installed.Should().ContainSingle("a published module is an installed module");
        Mesh.ServiceProvider.GetRequiredService<IBootBuiltProbe>().Answer().Should().Be("boot-built");
    }
}

/// <summary>
/// MeshWeaver#6584 — <b>a module's scoped service is the CALLER's.</b> Asked from inside a scope — a
/// Blazor circuit's, a request's, a hub's — a module's scoped registration is built in a module scope
/// that caller owns, against the caller's own scoped state (Blazor's <c>NavigationManager</c> and
/// <c>IJSRuntime</c>, here <see cref="CircuitBoundState"/>), and ends with the caller. Before, it was one
/// instance per module generation, shared by every circuit and built against a root child scope no
/// circuit had initialised — a component library's <c>ThemeService</c> for every user at once, and a
/// <c>DialogService</c> that threw on its first real resolve. Both roads the root reaches a module by are
/// measured: a type the module declares (<see cref="ModuleOwnedRootSource"/>) and a platform class it
/// registers (the <c>Current</c> forwarder).
/// </summary>
public sealed class AModulesScopedServicesAreTheCallersTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool CircuitBound => true;

    private Type ModuleDialog =>
        (Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry"))
        .Assembly.GetType("MeshWeaver.Test.BootBuilt.CircuitDialog")
        ?? throw new Xunit.Sdk.XunitException("CircuitDialog is not in the held module");

    private static CircuitBoundState StateOf(object service) =>
        (CircuitBoundState)service.GetType().GetProperty("State")!.GetValue(service)!;

    // A circuit: a scope of the root, whose state the circuit initialises when it starts.
    private AsyncServiceScope Circuit(bool initialise = true)
    {
        var circuit = Mesh.ServiceProvider.CreateAsyncScope();
        if (initialise)
            circuit.ServiceProvider.GetRequiredService<CircuitBoundState>().Initialize();
        return circuit;
    }

    [Fact]
    public async Task TwoCircuits_EachResolveTheirOwnInstance_BuiltOverTheirOwnCircuit()
    {
        await using var first = Circuit();
        await using var second = Circuit();
        foreach (var type in new[] { ModuleDialog, typeof(CircuitPlatformService) })
        {
            var fromFirst = first.ServiceProvider.GetRequiredService(type);
            var fromSecond = second.ServiceProvider.GetRequiredService(type);

            fromFirst.Should().BeSameAs(first.ServiceProvider.GetRequiredService(type),
                $"{type.Name} is scoped: within one circuit it is that circuit's one instance");
            fromFirst.Should().NotBeSameAs(fromSecond,
                $"two circuits are two scopes — one {type.Name} shared between them is one user's state served to another");
            StateOf(fromFirst).Should().BeSameAs(first.ServiceProvider.GetRequiredService<CircuitBoundState>(),
                $"{type.Name} is built against ITS circuit's state — the NavigationManager that circuit initialised");
            StateOf(fromSecond).Should().BeSameAs(second.ServiceProvider.GetRequiredService<CircuitBoundState>());
        }
    }

    [Fact]
    public async Task EndingACircuit_EndsItsModuleScope_AndNoOtherCircuits()
    {
        var ending = Circuit();
        await using var staying = Circuit();
        var endingState = ending.ServiceProvider.GetRequiredService<CircuitBoundState>();
        var stayingState = staying.ServiceProvider.GetRequiredService<CircuitBoundState>();
        ending.ServiceProvider.GetRequiredService(ModuleDialog);
        staying.ServiceProvider.GetRequiredService(ModuleDialog);

        await ending.DisposeAsync();

        endingState.Released.Should().Be(1,
            "the module scope the circuit owned ends with the circuit — a Blazor circuit's scope is disposed asynchronously");
        stayingState.Released.Should().Be(0, "another circuit's module scope is its own");
        staying.ServiceProvider.GetRequiredService(ModuleDialog).Should().NotBeNull("the other circuit keeps being served");
    }

    /// <summary>
    /// The negative control: asked with NO caller — off the root provider itself, which is where every
    /// resolve of a module service used to land — the same registration is built against state no circuit
    /// initialised, and throws exactly as a component library's <c>DialogService</c> did. So the test above tells a
    /// caller-bound build from a lucky one.
    /// </summary>
    [Fact]
    public void WithNoCaller_TheSameRegistration_IsBuiltOverStateNoCircuitInitialised()
    {
        var refused = Record.Exception(() => Mesh.ServiceProvider.GetRequiredService(ModuleDialog));
        refused.Should().NotBeNull("resolved in the module's own root, its circuit state comes from no circuit");
        refused!.ToString().Should().Contain("has not been initialized");

        using var uninitialised = Mesh.ServiceProvider.CreateScope();
        Record.Exception(() => uninitialised.ServiceProvider.GetRequiredService(ModuleDialog))!.ToString()
            .Should().Contain("has not been initialized",
                "a circuit that has not initialised its own state is not rescued by another's");
    }
}
