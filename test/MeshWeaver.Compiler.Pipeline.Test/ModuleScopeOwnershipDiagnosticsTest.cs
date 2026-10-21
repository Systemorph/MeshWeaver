using System.Collections.Concurrent;
using Autofac;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>Every log entry written through it, kept per instance (never static).</summary>
internal sealed class CapturedLogs : ILoggerFactory, ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries = new();

    /// <summary>The ownership diagnostics written so far.</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Ownership =>
        entries.Where(e => e.Message.Contains(ModuleScopeOwnership.Marker, StringComparison.Ordinal)).ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

/// <summary>A platform class a module registers as a SINGLETON — the generation owns it.</summary>
public sealed class DiagnosedSingleton : IDisposable
{
    private int disposed;

    /// <summary>How many times it was disposed.</summary>
    public int Disposed => Volatile.Read(ref disposed);

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref disposed);
}

/// <summary>A platform class a module registers SCOPED — one per caller, like Radzen's ThemeService.</summary>
public sealed class DiagnosedPerCircuit : IDisposable
{
    private int disposed;

    /// <summary>How many times it was disposed.</summary>
    public int Disposed => Volatile.Read(ref disposed);

    /// <inheritdoc />
    public void Dispose() => Interlocked.Increment(ref disposed);
}

/// <summary>
/// MeshWeaver#6584 and MeshWeaver#6588 never wrote a log line while they were live, so a quiet production
/// log proved nothing. <see cref="ModuleScopeOwnership"/> writes the lines the defects WOULD write. Each
/// test here re-introduces a defect deliberately — the route the code took before the fix — and shows the
/// warning fires (the negative control), and shows the correct route writes nothing. Real Autofac
/// containers, built exactly as the mesh builds a module's (<see cref="ModuleServiceProvider.CreateForCallers"/>).
/// </summary>
public sealed class ModuleScopeOwnershipDiagnosticsTest
{
    private sealed class Mesh
    {
        public required CapturedLogs Logs { get; init; }
        public required IServiceProvider Root { get; init; }
        public required IServiceProvider Module { get; init; }

        public ILifetimeScope Circuit(string tag) => Root.GetRequiredService<ILifetimeScope>().BeginLifetimeScope(tag);
    }

    /// <summary>A root with a module container beside it; the root forwards the module's two classes the
    /// way the <c>Current</c> route does — owned by the resolving scope (the #6588 defect) or not (the fix).</summary>
    private static Mesh Build(bool forwardersOwnWhatTheyReturn)
    {
        var logs = new CapturedLogs();
        IServiceProvider? module = null;
        IServiceCollection root = new ServiceCollection();
        root.AddSingleton<ILoggerFactory>(logs);
        root.AddSingleton(new ModuleInstanceOwners());
        foreach (var type in new[] { typeof(DiagnosedSingleton), typeof(DiagnosedPerCircuit) })
        {
            Func<IServiceProvider, object> forward = sp =>
                ModuleServiceProvider.ResolveAt(module!, type, 0, ModuleServiceProvider.CallerOfRootScope(sp));
            root.Add(forwardersOwnWhatTheyReturn
                ? ServiceDescriptor.Transient(type, forward)
                : NotOwnedServiceDescriptor.Forwarder(type, forward, ServiceLifetime.Transient));
        }
        var rootProvider = root.CreateMeshWeaverServiceProvider();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<DiagnosedSingleton>();
        services.AddScoped<DiagnosedPerCircuit>();
        module = ModuleServiceProvider.CreateForCallers(services, rootProvider, _ => false, [],
            _ => Array.Empty<Func<ILifetimeScope?, object>>());
        return new Mesh { Logs = logs, Root = rootProvider, Module = module };
    }

    [Fact]
    public void AScopedServiceBuiltForNoCaller_Warns_TheNegativeControl()
    {
        var mesh = Build(forwardersOwnWhatTheyReturn: false);

        // The route before #6586: resolved off the module container with no caller.
        var shared = ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0);
        ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0).Should()
            .BeSameAs(shared, "the defect: one instance per generation, shared by every caller");

        var warnings = mesh.Logs.Ownership;
        warnings.Should().ContainSingle("the defect writes its line, once per registration");
        warnings[0].Level.Should().Be(LogLevel.Warning);
        warnings[0].Category.Should().Be(ModuleScopeOwnership.Category);
        warnings[0].Message.Should().Contain("MeshWeaver#6584").And.Contain(nameof(DiagnosedPerCircuit))
            .And.Contain("root scope");
    }

    [Fact]
    public void EachCallersOwnInstance_WritesNothing()
    {
        var mesh = Build(forwardersOwnWhatTheyReturn: false);
        using var a = mesh.Circuit("circuit-a");
        using var b = mesh.Circuit("circuit-b");

        var ofA = ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0, a);
        var ofB = ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0, b);
        ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0, a).Should().BeSameAs(ofA);
        ofB.Should().NotBeSameAs(ofA, "each caller has its own instance");
        ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedSingleton), 0, a).Should().NotBeNull();

        mesh.Logs.Ownership.Should().BeEmpty("the correct route writes nothing — a warning here would be noise in production");
    }

    [Fact]
    public void OneCallersInstanceServedToAnother_Warns_TheNegativeControl()
    {
        var mesh = Build(forwardersOwnWhatTheyReturn: false);
        using var a = mesh.Circuit("circuit-a");
        using var b = mesh.Circuit("circuit-b");
        var ofA = ModuleServiceProvider.ResolveAt(mesh.Module, typeof(DiagnosedPerCircuit), 0, a);

        ModuleServiceProvider.ReportIfServedAcrossCallers(mesh.Module, ofA, a);
        mesh.Logs.Ownership.Should().BeEmpty("served to its own caller: correct");

        // The defect's other shape: a serving point hands circuit A's instance to circuit B.
        ModuleServiceProvider.ReportIfServedAcrossCallers(mesh.Module, ofA, b);
        var warnings = mesh.Logs.Ownership;
        warnings.Should().ContainSingle();
        warnings[0].Level.Should().Be(LogLevel.Warning);
        warnings[0].Message.Should().Contain("MeshWeaver#6584").And.Contain("circuit-a").And.Contain("circuit-b");
    }

    [Fact]
    public void AScopeDisposingWhatItWasForwarded_Warns_TheNegativeControl()
    {
        // The route before #6591: a forwarder the resolving scope owns.
        var mesh = Build(forwardersOwnWhatTheyReturn: true);
        var circuit = mesh.Circuit("circuit-a");
        var singleton = circuit.Resolve<IServiceProvider>().GetRequiredService<DiagnosedSingleton>();
        var perCircuit = circuit.Resolve<IServiceProvider>().GetRequiredService<DiagnosedPerCircuit>();
        mesh.Logs.Ownership.Should().BeEmpty("the line is written as the scope ends — when the disposal happens");

        circuit.Dispose();

        singleton.Disposed.Should().Be(1, "the defect is really in place: the circuit disposed the module's singleton");
        perCircuit.Disposed.Should().Be(2, "and the scoped instance twice — its module scope, then the circuit again");
        var warnings = mesh.Logs.Ownership;
        warnings.Should().HaveCount(2, "one line per forwarded registration");
        warnings.Should().OnlyContain(w => w.Level == LogLevel.Warning && w.Message.Contains("MeshWeaver#6588"));
        warnings.Should().Contain(w => w.Message.Contains(nameof(DiagnosedSingleton)))
            .And.Contain(w => w.Message.Contains(nameof(DiagnosedPerCircuit)));
    }

    [Fact]
    public void ANotOwnedForwarder_WritesNothing_AndDisposesNothingItWasForwarded()
    {
        var mesh = Build(forwardersOwnWhatTheyReturn: false);
        var circuit = mesh.Circuit("circuit-a");
        var singleton = circuit.Resolve<IServiceProvider>().GetRequiredService<DiagnosedSingleton>();
        var perCircuit = circuit.Resolve<IServiceProvider>().GetRequiredService<DiagnosedPerCircuit>();

        circuit.Dispose();

        singleton.Disposed.Should().Be(0);
        perCircuit.Disposed.Should().Be(1, "disposed once, by the module scope its circuit owned");
        mesh.Logs.Ownership.Should().BeEmpty("the correct route writes nothing");
    }

    /// <summary>The happy path through the REAL wiring — a generic host, the mesh's module registry, a
    /// module installed in its own load context: the full circuit lifecycle writes no ownership line, and
    /// the registry the diagnostics read is registered.</summary>
    [Fact(Timeout = 60000)]
    public async Task OnAHost_TheCorrectRoute_WritesNothing()
    {
        var logs = new CapturedLogs();
        var root = Path.Combine(Path.GetTempPath(), "ownership-diagnostics-" + Guid.NewGuid().ToString("N"));
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(logs);
        builder.UseMeshWeaver(new Address("mesh", "ownership-diagnostics"),
            mesh => mesh
                .ConfigureServices(services => services.AddScoped<CircuitBoundState>())
                .InstallAssemblies(ForwarderOwnershipModule.Write(root)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetService<ModuleInstanceOwners>().Should()
            .NotBeNull("the mesh registers the registry the #6588 diagnostic reads — without it the line could never fire");
        await ForwarderOwnershipModule.EndingOneCircuit_DisposesNothingTheModuleOwns(host.Services);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.Ownership.Should().BeEmpty("nothing on the happy path");
    }
}
