using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>
/// 🚨 <b>PRE-BOOT SERVICE SUBSTITUTION for the install-and-execute lane.</b>
///
/// <para>A test that proves a <i>composition</i> — a deliberately ABSENT
/// <c>EffectivePermissionsDelegate</c>, a recording scheduler, a fake <c>IGitHubRepoClient</c>,
/// an <c>IStorageAdapter</c> that hangs — cannot be written against an already-composed host. An
/// in-mesh <c>Tests</c> area boots INTO one, so those suites were the single largest blocker on
/// the way off xunit: measured 2026-09-01, 135 test classes across
/// <c>MeshWeaver.Plugins/src</c> and 145 registrations across core's <c>test/</c> substitute a
/// service in a <c>ConfigureMesh</c> override, and not one of them can express that as an
/// assertion inside a booted mesh.</para>
///
/// <para><b>The seam is a DECLARATION plus an APPLICATOR, and they are deliberately far apart.</b>
/// A suite DECLARES the mesh it needs by carrying
/// <c>public static MeshBuilder ConfigureMesh(MeshBuilder)</c> — an ordinary static method over an
/// ordinary framework type, in the suite's own assembly. It is INERT: a <see cref="MeshBuilder"/>
/// that nobody builds composes nothing. The APPLICATOR is this class, and it lives in
/// <c>mw-plugin-test</c> — a <c>tools/</c> console binary that no portal image contains.</para>
///
/// <para>🚨 <b>Why a production portal cannot be made to use this, by construction.</b> Three
/// independent reasons, any one of which is sufficient:</para>
/// <list type="number">
/// <item>There is <b>no marker to scan for</b> — no attribute, no interface, no base class, no
/// contract assembly. A portal has nothing it could enumerate even if it wanted to, and adding a
/// scan would be a visible new feature rather than a use of this one.</item>
/// <item>The only code that reads the declaration is <see cref="StaticTestRunner"/> in this
/// binary. <c>mw-plugin-test</c> ships in the tester image only; nothing under <c>src/</c>,
/// <c>memex/</c> or <c>clients/</c> references it (asserted by
/// <c>MeshTestSuiteTest.NoShippingProjectReferencesTheTester</c>), so the applicator is not
/// present in any portal at all.</item>
/// <item>🚨 The applicator can only <b>CREATE</b> a mesh. <see cref="Boot"/> takes a
/// <see cref="Type"/> and returns a brand-new, private, in-process mesh; there is no overload, no
/// property and no method anywhere on this type that accepts an existing
/// <see cref="IServiceProvider"/>, <see cref="IMessageHub"/> or <c>IServiceCollection</c>
/// (asserted by <c>MeshTestSuiteTest.TheFacilityCanNeverTouchAnExistingHost</c>). So even in the
/// impossible case that this code were loaded into a portal, the worst it could do is stand up a
/// throwaway mesh beside it. <b>It cannot swap a running host's <c>IStorageAdapter</c>, because
/// no API here takes a running host.</b></item>
/// </list>
///
/// <para><b>What a case receives.</b> Parameters are bound BY TYPE from a fixed, two-entry table
/// so that a suite needs no reference to this assembly:
/// <see cref="IServiceProvider"/> → the mesh's root provider (the mesh hub is
/// <c>services.GetRequiredService&lt;IMessageHub&gt;()</c>), and <see cref="IMessageHub"/> → a
/// fresh client hub. A case returning <see cref="IObservable{T}"/> of
/// <see cref="Unit"/> is awaited reactively — the same <c>Func&lt;IObservable&lt;Unit&gt;&gt;</c>
/// idiom the in-mesh <c>Tests</c> areas already use for hosted cases — so a migrated body never
/// has to block, and never has to add a line to <c>test/BlockingBridgeSites.allow</c>.</para>
/// </summary>
public sealed class MeshTestSuite : IDisposable
{
    /// <summary>The method name a suite uses to declare its mesh.</summary>
    public const string DeclarationName = "ConfigureMesh";

    /// <summary>How long one hosted service gets to start or stop before it is named.</summary>
    private static readonly TimeSpan HostedServiceBudget = TimeSpan.FromSeconds(30);

    private readonly List<IHostedService> startedHostedServices = [];
    private readonly TextWriter output;
    private readonly string runRoot;

    private MeshTestSuite(
        IServiceProvider services, IMessageHub mesh, IMessageHub client,
        TextWriter output, string runRoot)
    {
        Services = services;
        Mesh = mesh;
        Client = client;
        this.output = output;
        this.runRoot = runRoot;
    }

    /// <summary>The booted mesh's root service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The mesh hub.</summary>
    public IMessageHub Mesh { get; }

    /// <summary>A client hub registered with the mesh's routing service.</summary>
    public IMessageHub Client { get; }

    /// <summary>
    /// The suite's declaration, or null when it has none — in which case nothing about the class
    /// changes and its parameter-taking cases stay <c>NeedsMesh</c>, exactly as before.
    /// </summary>
    /// <param name="suite">The candidate test class.</param>
    /// <returns>The declaration method, or null.</returns>
    public static MethodInfo? FindDeclaration(Type suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var candidate = suite.GetMethod(
            DeclarationName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            binder: null,
            types: [typeof(MeshBuilder)],
            modifiers: null);
        return candidate?.ReturnType == typeof(MeshBuilder) ? candidate : null;
    }

    /// <summary>
    /// Whether every parameter of <paramref name="method"/> is one this lane can supply. A case
    /// with an unbindable parameter — a <c>LayoutAreaHost</c>, say — is NOT run here and NOT
    /// silently dropped: it stays classified for the mesh (area) lane.
    /// </summary>
    /// <param name="method">The candidate case.</param>
    /// <returns>True when the whole signature binds.</returns>
    public static bool CanBind(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.GetParameters().All(p => IsBindable(p.ParameterType));
    }

    private static bool IsBindable(Type t) =>
        t == typeof(IServiceProvider) || t == typeof(IMessageHub);

    /// <summary>Binds one case's arguments out of this booted mesh.</summary>
    /// <param name="method">The case to bind.</param>
    /// <returns>The argument array, in declaration order.</returns>
    public object?[] Bind(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return [.. method.GetParameters().Select(object? (p) =>
            p.ParameterType == typeof(IServiceProvider) ? Services : Client)];
    }

    /// <summary>
    /// Boots the mesh <paramref name="declaration"/> describes: a fresh service collection with
    /// logging and an empty configuration, the declaration applied to a new
    /// <see cref="MeshBuilder"/>, a per-suite assembly store and compilation cache so two suites
    /// compiling the same node path can never serve each other's bytes, then the hosted services
    /// this composition registered.
    ///
    /// <para>🚨 The declaration is applied to a builder this method CONSTRUCTS. Nothing existing is
    /// passed in and nothing existing can be reached — see the type remarks.</para>
    /// </summary>
    /// <param name="declaration">The suite's declaration, from <see cref="FindDeclaration"/>.</param>
    /// <param name="output">Where teardown diagnostics go.</param>
    /// <returns>The booted suite; dispose it when the class's cases are done.</returns>
    public static MeshTestSuite Boot(MethodInfo declaration, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(output);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var minLevel = Enum.TryParse<LogLevel>(
            Environment.GetEnvironmentVariable("MW_LOG_LEVEL"), ignoreCase: true, out var lvl)
            ? lvl
            : LogLevel.Warning;
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(minLevel);
            logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
        });
        services.AddOptions();

        var runRoot = Path.Combine(Path.GetTempPath(),
            $"mw-mesh-suite-{Environment.ProcessId}-{Guid.NewGuid():N}");

        var declared = declaration.Invoke(
            null,
            [new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())])
            as MeshBuilder
            ?? throw new InvalidOperationException(
                $"{declaration.DeclaringType?.Name}.{DeclarationName} returned null. The "
                + "declaration must return the builder it configured — a null one composes "
                + "nothing and would boot a mesh the suite never asked for, which is worse than "
                + "not booting at all.");

        // Per-SUITE isolation, applied after the declaration so a suite cannot accidentally share
        // it: AddInMemoryPersistence TryAdds a process-pid-scoped IAssemblyStore, so without the
        // REPLACE two suites compiling the same node path at a colliding version serve each other's
        // bytes (the failure MonolithMeshTestBase documents at length for test CLASSES).
        var builder = declared
            .ConfigureServices(s =>
            {
                s.RemoveAll<IAssemblyStore>();
                return s.AddFileSystemAssemblyStore(Path.Combine(runRoot, "assembly-store"));
            })
            .ConfigureServices(s => s.Configure<CompilationCacheOptions>(o =>
                o.CacheDirectory = Path.Combine(runRoot, "compilation-cache")));

        services.AddSingleton(builder.BuildHub);
        var provider = services.CreateMeshWeaverServiceProvider();
        var mesh = provider.GetRequiredService<IMessageHub>();

        // Pre-warm the NodeType hubs a runtime CreateNode would otherwise recurse on — the same
        // chicken-and-egg the gate and the monolith test base both pre-warm.
        foreach (var nodeTypePath in new[] { "AccessAssignment", "PartitionAccessPolicy" })
        {
            var typeNode = provider.FindStaticNode(nodeTypePath);
            if (typeNode?.HubConfiguration is { } config)
                _ = mesh.GetHostedHub(new Address(nodeTypePath), config);
        }

        var suite = new MeshTestSuite(provider, mesh, CreateClient(mesh), output, runRoot);

        // 🚨 NO identity is set here, on purpose. A suite that substitutes services is usually
        // ABOUT who may do what, so the lane must not decide that for it: whichever identity the
        // case establishes (AccessService.SetCircuitContext / SetHostIdentity) is the one under
        // test. A lane that logged an admin in first would make every fail-closed assertion pass
        // for the wrong reason.
        // 🚨 BOUNDED Task.Wait(TimeSpan), never the unbounded park. A hosted service that never
        // starts is a defect to NAME, not a boot to hang: expiry comes back as a bool, so the suite
        // says which service and the run continues. Same choice, for the same reason, as
        // HubDisposalJoin's joined.Wait(budget).
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            if (!hosted.StartAsync(CancellationToken.None).Wait(HostedServiceBudget))
                throw new InvalidOperationException(
                    $"the declared mesh's hosted service {hosted.GetType().Name} did not start "
                    + $"within {HostedServiceBudget.TotalSeconds:F0}s — a suite whose composition "
                    + "cannot start must fail loudly, never boot half-composed");
            suite.startedHostedServices.Add(hosted);
        }
        return suite;
    }

    /// <summary>
    /// Runs one case against this mesh and returns its failure text, or null when it passed.
    /// A case returning <see cref="IObservable{T}"/> of <see cref="Unit"/> is waited for through
    /// the one sanctioned reactive→Task bridge; anything else is invoked and its return ignored.
    /// </summary>
    /// <param name="method">The case.</param>
    /// <param name="budget">How long the case's stream may take to terminate.</param>
    /// <returns>Failure text (innermost first), or null on success.</returns>
    public string? Run(MethodInfo method, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(method);
        var returned = method.Invoke(null, Bind(method));
        if (returned is not IObservable<Unit> stream)
            return null;

        Exception? lateFault = null;
        // IgnoreElements + DefaultIfEmpty makes the FIRST notification the TERMINAL one, which is
        // what ObserveCompletion settles on — a case that emits progress values then faults must
        // report the fault, not the first value.
        var settled = stream
            .IgnoreElements()
            .DefaultIfEmpty(Unit.Default)
            .ObserveCompletion(ex => lateFault = ex);

        // Bounded Task.Wait(TimeSpan) on this case's OWN dedicated thread (StaticTestRunner gives
        // every case one and joins it): the parked thread is never a hub action block, a grain turn
        // or an Rx trampoline, so it cannot self-deadlock, and expiry comes back as a bool rather
        // than as a wedge. The same shape, for the same reason, as HubDisposalJoin's joined.Wait.
        if (!settled.Wait(budget))
            return $"TimeoutException: the case's stream did not terminate within "
                   + $"{budget.TotalSeconds:F0}s";
        if (settled.IsFaulted)
            return Innermost(settled.Exception!);
        return lateFault is null ? null : "late fault: " + Innermost(lateFault);
    }

    private static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
            e = e.InnerException;
        return $"{e.GetType().Name}: {e.Message.Split('\n', 2)[0].Trim()}";
    }

    private static IMessageHub CreateClient(IMessageHub mesh)
    {
        var routing = mesh.ServiceProvider.GetRequiredService<IRoutingService>();
        return mesh.ServiceProvider.CreateMessageHub(
            new Address("client", Guid.NewGuid().ToString("N")[..12]),
            configuration => configuration
                .AddMeshTypes()
                .AddData()
                .WithRequestTimeout(TimeSpan.FromSeconds(60))
                .WithInitialization(h => h.RegisterForDisposal(routing.RegisterStream(h))))!;
    }

    /// <summary>
    /// Tears the suite's mesh down: hosted services stopped in reverse, both hubs disposed AND
    /// JOINED (a started-but-unjoined disposal is the exit-139 use-after-dispose class), then the
    /// container and the run directory.
    /// </summary>
    public void Dispose()
    {
        foreach (var hosted in Enumerable.Reverse(startedHostedServices))
        {
            try
            {
                if (!hosted.StopAsync(CancellationToken.None).Wait(HostedServiceBudget))
                    output.WriteLine(
                        $"      [suite teardown] {hosted.GetType().Name} did not stop within "
                        + $"{HostedServiceBudget.TotalSeconds:F0}s");
            }
            catch (Exception ex)
            {
                output.WriteLine($"      [suite teardown] hosted service stop failed: {ex.Message}");
            }
        }
        Client.DisposeAndJoin(
            m => output.WriteLine($"      [suite teardown] {m}"), TimeSpan.FromSeconds(30));
        Mesh.DisposeAndJoin(
            m => output.WriteLine($"      [suite teardown] {m}"), TimeSpan.FromSeconds(30));
        (Services as IDisposable)?.Dispose();
        try
        {
            if (Directory.Exists(runRoot))
                Directory.Delete(runRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a verdict; the OS reclaims it.
        }
        catch (UnauthorizedAccessException)
        {
            // ditto.
        }
    }
}
