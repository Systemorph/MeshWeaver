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

    /// <summary>What a consumer's constructor does — Radzen's <c>DialogService</c> subscribes to
    /// <c>NavigationManager.LocationChanged</c>.</summary>
    public void Subscribe()
    {
        if (Volatile.Read(ref initialized) == 0)
            throw new InvalidOperationException("'CircuitBoundState' has not been initialized.");
    }
}

/// <summary>A platform CLASS a module registers SCOPED over circuit state — the route a Radzen service
/// takes when its assembly is in the image (<c>Current</c>).</summary>
public sealed class CircuitPlatformService
{
    /// <summary>Takes the circuit's state, as <c>DialogService</c> takes a <c>NavigationManager</c>.</summary>
    public CircuitPlatformService(CircuitBoundState state) => state.Subscribe();
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

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
            .InstallAssemblies(WriteModule(root, Broken, CircuitBound, ShipsReferences));

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

    private static string Source(bool broken, bool circuitBound, bool shipsReferences) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.BootBuilt.Module]
        namespace MeshWeaver.Test.BootBuilt;
        public sealed class Probe : MeshWeaver.Graph.Test.IBootBuiltProbe { public string Answer() => "boot-built"; }
        public sealed class Fragile
        {
            public Fragile() { {{(broken ? "throw new System.InvalidOperationException(\"cannot be built by design\");" : "")}} }
        }
        public sealed class CircuitDialog
        {
            public CircuitDialog(MeshWeaver.Graph.Test.CircuitBoundState state) => state.Subscribe();
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
                        return services;
                    })];
        }
        """;

    // A module that registers an installed module through its OWN services — a further reference
    // assembly (MeshWeaver#6557): the root forwards it, and once the module is withdrawn that member
    // must be absent from the set, never a forwarder that can only throw.
    private const string ContributedInstalledModule =
        "services.AddSingleton(new MeshWeaver.Mesh.InstalledModuleAssembly(typeof(Probe).Assembly));";

    // The Radzen shape: scoped services over circuit state (a module class and a platform class), a
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

    /// <summary>Compiles the test module under <paramref name="directoryRoot"/> and returns its entry DLL —
    /// for hosts this base does not build (MeshWeaver#6557's plain MS DI host).</summary>
    internal static string WriteModule(string directoryRoot, bool broken, bool circuitBound, bool shipsReferences = false) =>
        Write(directoryRoot, Source(broken, circuitBound, shipsReferences));

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
/// MeshWeaver#6556 — the Radzen shape. A module registers services that belong to a CIRCUIT: scoped
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
