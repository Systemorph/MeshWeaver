using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A platform CLASS a module registers as a SINGLETON — the <c>Current</c> route. One instance
/// for every circuit; disposing it under them is the defect (MeshWeaver#6588).</summary>
public sealed class SharedPlatformSingleton : IDisposable
{
    private int disposed;

    /// <summary>How many times it was disposed.</summary>
    public int Disposed => Volatile.Read(ref disposed);

    /// <summary>What a circuit does with it — throws once it was disposed, as a disposed service does.</summary>
    public string Serve()
    {
        ObjectDisposedException.ThrowIf(Disposed > 0, this);
        return "served";
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref disposed);
}

/// <summary>A platform CLASS a module registers SCOPED — the <c>Current</c> route, one per circuit.</summary>
public sealed class CircuitPlatformResource : IDisposable
{
    private int disposed;

    /// <summary>How many times it was disposed.</summary>
    public int Disposed => Volatile.Read(ref disposed);

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref disposed);
}

/// <summary>A platform INTERFACE that is itself disposable, which a module implements SCOPED — the
/// <c>Proxy</c> route, whose proxy forwards <c>Dispose</c> to the module's instance. The module's
/// instance records its disposals on its circuit's <see cref="CircuitBoundState"/>: the proxy cannot be
/// asked once its circuit has ended.</summary>
public interface IDisposableCircuitResource : IDisposable
{
    /// <summary>Answers while its circuit lives.</summary>
    string Serve();
}

/// <summary>Compiles and installs the forwarder-ownership test module.</summary>
internal static class ForwarderOwnershipModule
{
    /// <summary>The module's simple name.</summary>
    public const string Name = "MeshWeaver.Test.ForwarderOwnership";

    private const string Source = """
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.ForwarderOwnership.Module]
        namespace MeshWeaver.Test.ForwarderOwnership;
        public sealed class CircuitResource(MeshWeaver.Graph.Test.CircuitBoundState state) : MeshWeaver.Graph.Test.IDisposableCircuitResource
        {
            public string Serve() => "served";
            public void Dispose() => state.Release();
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("ForwarderOwnershipProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<MeshWeaver.Graph.Test.SharedPlatformSingleton>();
                        services.AddScoped<MeshWeaver.Graph.Test.CircuitPlatformResource>();
                        services.AddScoped<MeshWeaver.Graph.Test.IDisposableCircuitResource, CircuitResource>();
                        return services;
                    })];
        }
        """;

    /// <summary>Writes the module under <paramref name="root"/> and returns the assembly's path.</summary>
    public static string Write(string root)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(SharedPlatformSingleton).Assembly.Location));
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

    /// <summary>
    /// The measurements, against a mesh's ROOT provider — whichever host built it. A "circuit" is a scope
    /// of the root, as a Blazor circuit's is.
    /// </summary>
    public static async Task EndingOneCircuit_DisposesNothingTheModuleOwns(IServiceProvider root)
    {
        root.GetRequiredService<ModuleContexts>().Current(Name).Should()
            .NotBeNull("the module is held in its own load context, so its services reach the root through forwarders");

        var ending = root.CreateAsyncScope();
        await using var staying = root.CreateAsyncScope();
        var singleton = ending.ServiceProvider.GetRequiredService<SharedPlatformSingleton>();
        var resource = ending.ServiceProvider.GetRequiredService<CircuitPlatformResource>();
        ending.ServiceProvider.GetRequiredService<IDisposableCircuitResource>().Serve().Should().Be("served");
        var endingState = ending.ServiceProvider.GetRequiredService<CircuitBoundState>();
        staying.ServiceProvider.GetRequiredService<SharedPlatformSingleton>().Should()
            .BeSameAs(singleton, "a module singleton is one instance for every circuit");
        var otherResource = staying.ServiceProvider.GetRequiredService<CircuitPlatformResource>();
        staying.ServiceProvider.GetRequiredService<IDisposableCircuitResource>().Serve().Should().Be("served");
        var stayingState = staying.ServiceProvider.GetRequiredService<CircuitBoundState>();

        await ending.DisposeAsync();

        singleton.Disposed.Should().Be(0,
            "the singleton belongs to the module generation — one circuit ending must not dispose it under every other user");
        staying.ServiceProvider.GetRequiredService<SharedPlatformSingleton>().Serve().Should().Be("served",
            "the other circuit goes on using the module singleton");
        resource.Disposed.Should().Be(1,
            "a scoped instance is disposed exactly once — by the module scope its circuit owned, not again by the circuit itself");
        endingState.Released.Should().Be(1,
            "the proxy route too: disposing the circuit's proxy is not a second disposal of the module's instance");
        otherResource.Disposed.Should().Be(0, "another circuit's instance is its own");
        stayingState.Released.Should().Be(0);

        await staying.DisposeAsync();
        otherResource.Disposed.Should().Be(1, "the control: a circuit's end does dispose ITS scoped instance, once");
        stayingState.Released.Should().Be(1, "the control, on the proxy route");
        singleton.Disposed.Should().Be(0, "no circuit owns the module singleton");
    }
}

/// <summary>
/// MeshWeaver#6588 — <b>a module forwarder owns nothing it returns.</b> The root's forwarders of a held
/// module's platform-typed services were factory registrations the RESOLVING scope owned, so a Blazor
/// circuit ending disposed what it had been handed: a module SINGLETON — under every other live user —
/// and a module SCOPED instance a second time, after the module scope its circuit owned (MeshWeaver#6584)
/// had disposed it. Measured on the test mesh's root.
/// </summary>
public sealed class AModuleForwarderOwnsNothingItReturnsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forwarder-ownership-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
            .InstallAssemblies(ForwarderOwnershipModule.Write(root));

    [Fact]
    public Task EndingOneCircuit_DisposesNothingTheModuleOwns() =>
        ForwarderOwnershipModule.EndingOneCircuit_DisposesNothingTheModuleOwns(Mesh.ServiceProvider);
}

/// <summary>
/// The same measurement on the two GENERIC hosts a portal or a silo runs on — the
/// <see cref="IHostApplicationBuilder"/> path (<see cref="MeshHostApplicationBuilder"/>) and the legacy
/// <see cref="IHostBuilder"/> path (<see cref="MeshHostBuilder"/>), both building the root through
/// <see cref="MessageHubServiceProviderFactory"/>.
/// </summary>
public sealed class AModuleForwarderOwnsNothingItReturnsOnAGenericHostTest
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forwarder-ownership-host-" + Guid.NewGuid().ToString("N"));

    [Fact(Timeout = 60000)]
    public async Task OnAHostApplicationBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.UseMeshWeaver(new Address("mesh", "forwarder-ownership-app"),
            mesh => mesh
                .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
                .InstallAssemblies(ForwarderOwnershipModule.Write(root)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await ForwarderOwnershipModule.EndingOneCircuit_DisposesNothingTheModuleOwns(host.Services);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 60000)]
    public async Task OnALegacyHostBuilder()
    {
        var builder = new HostBuilder();
        new MeshHostBuilder(builder, new Address("mesh", "forwarder-ownership-legacy"))
            .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
            .InstallAssemblies(ForwarderOwnershipModule.Write(root));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await ForwarderOwnershipModule.EndingOneCircuit_DisposesNothingTheModuleOwns(host.Services);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// A <see cref="NotOwnedServiceDescriptor"/> is registered at ITS OWN POSITION — which registration of a
/// service is the default, and the order of an <c>IEnumerable</c> of it, are exactly the collection's —
/// and its instance is never disposed by the scope that resolved it. Real service containers.
/// </summary>
public sealed class ANotOwnedForwarderKeepsItsPositionTest
{
    private sealed class Owned : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void TheDefaultAndTheEnumerationOrder_AreTheCollections()
    {
        var first = new Owned();
        var forwarded = new Owned();
        var last = new Owned();
        IServiceCollection services = new ServiceCollection();
        services.AddTransient(_ => first);
        services.Add(NotOwnedServiceDescriptor.Forwarder(typeof(Owned), _ => forwarded, ServiceLifetime.Transient));
        IServiceCollection forwarderLast = new ServiceCollection();
        forwarderLast.Add(services[0]);
        forwarderLast.Add(services[1]);
        var withForwarderLast = forwarderLast.CreateMeshWeaverServiceProvider();
        withForwarderLast.GetRequiredService<Owned>().Should().BeSameAs(forwarded, "registered last, the forwarder is the default");
        withForwarderLast.GetServices<Owned>().Should().Equal(first, forwarded);

        services.AddTransient(_ => last);
        var withForwarderInTheMiddle = services.CreateMeshWeaverServiceProvider();
        withForwarderInTheMiddle.GetRequiredService<Owned>().Should().BeSameAs(last,
            "a registration after the forwarder stays the default — the forwarder kept its position");
        withForwarderInTheMiddle.GetServices<Owned>().Should().Equal([first, forwarded, last],
            "the enumeration is in collection order");
    }

    [Fact]
    public void TheResolvingScope_DisposesWhatItOwns_AndNotWhatItWasForwarded()
    {
        var owned = new Owned();
        var forwarded = new Owned();
        IServiceCollection services = new ServiceCollection();
        services.AddTransient(_ => owned);
        services.Add(NotOwnedServiceDescriptor.KeyedForwarder(typeof(Owned), "forwarded", (_, _) => forwarded, ServiceLifetime.Transient));
        var provider = services.CreateMeshWeaverServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Owned>().Should().BeSameAs(owned);
            scope.ServiceProvider.GetRequiredKeyedService<Owned>("forwarded").Should().BeSameAs(forwarded);
        }

        owned.Disposed.Should().BeGreaterThan(0, "the control: an ordinary factory registration IS owned by the scope");
        forwarded.Disposed.Should().Be(0, "a forwarder's instance belongs to whoever built it");
        MeshServiceProvider.From(provider).Should().NotBeNull("an service container, as every mesh root is");
    }

    /// <summary>
    /// A keyed forwarder whose key is NULL is the UNKEYED service — as <c>Populate</c> reads any descriptor
    /// with a null key — and is still not owned by the scope that resolved it.
    /// </summary>
    [Fact]
    public void ANullKeyedForwarder_IsTheUnkeyedService_AndNotOwned()
    {
        var forwarded = new Owned();
        object? keyPassed = "not called";
        IServiceCollection services = new ServiceCollection();
        services.Add(NotOwnedServiceDescriptor.KeyedForwarder(typeof(Owned), null, (_, key) =>
        {
            keyPassed = key;
            return forwarded;
        }, ServiceLifetime.Transient));
        var provider = services.CreateMeshWeaverServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Owned>().Should().BeSameAs(forwarded,
                "a null key registers the unkeyed service");
            scope.ServiceProvider.GetRequiredKeyedService<Owned>(null).Should().BeSameAs(forwarded,
                "and asking with a null key answers it too");
        }

        keyPassed.Should().BeNull("the keyed factory is still the one invoked, with the null key");
        forwarded.Disposed.Should().Be(0, "a forwarder's instance belongs to whoever built it");
    }
}
