using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ServiceProvider;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Where each instance of a module's SCOPED <see cref="INodeValidator"/> was built — a root singleton of the
/// test mesh (never static), written to by the validator's own constructor.
/// </summary>
public sealed class ModuleValidatorBuilds
{
    private ImmutableList<string?> callers = ImmutableList<string?>.Empty;

    /// <summary>The caller that owned the module scope of each build, in order; null = the module
    /// container's own root scope, which no caller owns (MeshWeaver#6584).</summary>
    public IReadOnlyList<string?> Callers => Volatile.Read(ref callers);

    /// <summary>Records a build in the module scope <paramref name="provider"/> belongs to.</summary>
    public void Record(IServiceProvider provider)
    {
        var caller = ModuleServiceProvider.CallerOfModuleScope(MeshServiceProvider.From(provider));
        ImmutableInterlocked.Update(ref callers, list => list.Add(caller?.Tag?.ToString() ?? (caller is null ? null : "untagged caller")));
    }
}

/// <summary>Compiles the module: one write-only <see cref="INodeValidator"/>, registered SCOPED — the
/// shape of the Hosting module's <c>InstanceActionRequestWriteGuard</c> and the AI module's
/// <c>ModelCeilingWriteGuard</c>, which production named on 2026-10-22.</summary>
internal static class ScopedValidatorModule
{
    /// <summary>The module's simple name.</summary>
    public const string Name = "MeshWeaver.Test.ScopedValidator";

    /// <summary>The node the module declares — what the query reads.</summary>
    public const string ProbePath = "ScopedValidatorProbe";

    private const string Source = """
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.ScopedValidator.Module]
        namespace MeshWeaver.Test.ScopedValidator;
        public sealed class WriteOnlyGuard : MeshWeaver.Mesh.Services.INodeValidator
        {
            public WriteOnlyGuard(System.IServiceProvider scope, MeshWeaver.Graph.Test.ModuleValidatorBuilds builds) =>
                builds.Record(scope);
            public System.Collections.Generic.IReadOnlyCollection<MeshWeaver.Mesh.Security.NodeOperation> SupportedOperations { get; } =
                [MeshWeaver.Mesh.Security.NodeOperation.Create, MeshWeaver.Mesh.Security.NodeOperation.Update];
            public System.IObservable<MeshWeaver.Mesh.Services.NodeValidationResult> Validate(
                MeshWeaver.Mesh.Services.NodeValidationContext context) =>
                System.Reactive.Linq.Observable.Return(MeshWeaver.Mesh.Services.NodeValidationResult.Valid());
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("ScopedValidatorProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddScoped<MeshWeaver.Mesh.Services.INodeValidator, WriteOnlyGuard>();
                        return services;
                    })];
        }
        """;

    /// <summary>Writes the module under <paramref name="root"/> and returns the assembly's path.</summary>
    public static string Write(string root)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(ModuleValidatorBuilds).Assembly.Location));
        var compilation = CSharpCompilation.Create(Name, [CSharpSyntaxTree.ParseText(Source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Name}@g1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Name + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}

/// <summary>
/// MeshWeaver#6584, the shape production measured after #7027 rolled: two module write guards — scoped
/// <see cref="INodeValidator"/>s — were built in the module container's ROOT scope, which no caller owns.
/// The route: the mesh's query providers are ROOT singletons, and they took the validator set from the
/// root container (<c>IEnumerable&lt;Lazy&lt;INodeValidator&gt;&gt;</c>), so a module's scoped validator
/// was forwarded for no caller and built in the module's root scope — one instance for every caller of
/// the generation — only to read its <see cref="INodeValidator.SupportedOperations"/> and be discarded
/// as a non-Read validator. A root singleton holding scoped services is a captive dependency; the set
/// is now resolved in a scope the providers' own holder owns (<c>SingletonNodeValidators</c>), and a
/// module's scoped validator is built in the module scope THAT caller owns.
/// </summary>
public sealed class AModuleNodeValidatorIsNeverBuiltForNoCallerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly string root = Path.Combine(Path.GetTempPath(), "scoped-validator-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ModuleValidatorBuilds>())
            .InstallAssemblies(ScopedValidatorModule.Write(root));

    [Fact(Timeout = 120_000)]
    public async Task AQueryReadingThroughTheValidators_BuildsTheModulesScopedValidatorForACaller()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(ScopedValidatorModule.Name).Should()
            .NotBeNull("the module is held in its own load context, so its validator reaches the root through a forwarder");

        // A node in storage, so the query below is answered by the storage query provider — whose Read
        // filter runs every row through the validator set and so materialises all of it, the module's
        // write-only guard among it.
        var node = new MeshNode("validated-" + Guid.NewGuid().ToString("N")[..8], TestPartition)
        {
            Name = "read through the validators",
            NodeType = "Markdown",
        };
        await MeshQuery.CreateNode(node).Should().Within(Budget)
            .Emit("the node is stored", cancellationToken: ct);

        // A secured query as the signed-in user, not as System.
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{node.Path}"))
            .Where(change => change.Items.Any(item => item.Path == node.Path))
            .Should().Within(Budget)
            .Emit("the stored node is readable through the query providers' Read filter", cancellationToken: ct);

        var builds = Mesh.ServiceProvider.GetRequiredService<ModuleValidatorBuilds>().Callers;
        Output.WriteLine($"module validator builds, by owning caller: [{string.Join(", ", builds.Select(c => c ?? "<no caller>"))}]");
        builds.Where(caller => caller is null).Should().BeEmpty(
            "a module's scoped validator built in the module container's root scope is one instance for every caller "
            + "of the generation, built against none (MeshWeaver#6584) — the query providers must resolve it for a "
            + "caller that owns it");

        // The control: the query providers' own validator set really was materialised by that query, so the
        // empty 'no caller' list above is a reading of the route that used to build it for no caller.
        var held = Mesh.ServiceProvider.GetRequiredService<SingletonNodeValidators>().Validators.ToArray();
        held.Should().NotBeEmpty("the mesh registers node validators");
        held.Where(validator => !validator.IsValueCreated).Should().BeEmpty(
            "the query's Read filter builds every validator of the set the query providers hold");
    }
}
