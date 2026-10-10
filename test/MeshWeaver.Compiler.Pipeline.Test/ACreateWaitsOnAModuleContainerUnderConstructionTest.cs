using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>A PLATFORM interface the test module implements with a service that is slow to build.</summary>
public interface IModuleServiceUnderConstruction
{
    /// <summary>Any call — the first one builds the module's instance.</summary>
    string Touch();
}

/// <summary>
/// The switch the test holds a module-container construction with, and the journal of what the
/// module's container built. A platform singleton; instance state only.
/// </summary>
public sealed class ModuleConstructionGate
{
    private int armed;
    private int entered;
    private int released;
    private int handlersBuilt;

    /// <summary>How long a parked construction waits for its release before giving up by itself.</summary>
    public static readonly TimeSpan ParkBound = TimeSpan.FromSeconds(45);

    /// <summary>From now on the slow service's construction parks until <see cref="Release"/>.</summary>
    public void Arm() => Volatile.Write(ref armed, 1);

    /// <summary>Whether a construction is parked inside the module's container right now.</summary>
    public bool Entered => Volatile.Read(ref entered) == 1;

    /// <summary>How many times the module's container built its post-creation handler.</summary>
    public int HandlersBuilt => Volatile.Read(ref handlersBuilt);

    /// <summary>Lets the parked construction finish.</summary>
    public void Release() => Volatile.Write(ref released, 1);

    /// <summary>Called by the module from INSIDE its slow service's construction.</summary>
    public void Hold()
    {
        if (Volatile.Read(ref armed) == 0)
            return;
        Volatile.Write(ref entered, 1);
        SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, ParkBound);
    }

    /// <summary>Called by the module's handler registration when the container builds the handler.</summary>
    public void NoteHandlerBuilt() => Interlocked.Increment(ref handlersBuilt);
}

/// <summary>
/// MeshWeaver#6391 — <b>every create in the mesh waits on every module's container, whatever the
/// node's type.</b>
///
/// <para>A post-creation handler a module registers reaches the mesh as a forwarding proxy
/// (<see cref="ModuleServiceProxy"/>), and every call on the proxy — <c>Matches</c> included —
/// resolves the module's instance from the module's OWN container first. Autofac builds a
/// container's shared instances one at a time, so while ANY singleton of that module is under
/// construction, the handler behind the proxy cannot be built, and the create that only wanted to
/// learn "this handler does not apply to me" waits.</para>
///
/// <para>That is the shape of the boot-time stalls: 18 of 18 on a freshly started pod, all
/// System-written creates no handler applies to (<c>Admin/_Notification/…</c>, the platform's boot
/// <c>_Activity</c>), all reading "its post-creation handlers had not finished within 30s" — which
/// named every handler at once, and so none. This test holds one module singleton's construction,
/// creates a node of an unrelated type, and pins what the verdict now says: the row is written,
/// and the leg is waiting at <c>matching</c> the module's handler, named by module and
/// registration without resolving it.</para>
///
/// <para>🚨 It pins the NAMING and the mechanism, not a cure. What holds a module's container at
/// boot in production is not established by any log; while it is held, the create cannot be
/// answered `Ok`, because whether a handler applies is the handler's own answer.</para>
///
/// <para><b>Why the verdict is read off the log first.</b> The in-memory store completes its write
/// on the node-operation hub's own turn, so the parked <c>Matches</c> parks that hub's pump with
/// it, and the reply the deadline posts stays queued behind it until the release. (On a database
/// the write completes on an I/O thread, the pump stays idle and the reply travels at once — which
/// is what the production trail shows.) The log line is written by the deadline's own thread, so
/// it is the signal that exists while the container is still held; the reply is then read after
/// the release and must carry the same verdict.</para>
/// </summary>
public sealed class ACreateWaitsOnAModuleContainerUnderConstructionTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.HeldContainer";

    /// <summary>Rung 1 of the ladder for this mesh — the create's verdict deadline.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>How long past the budget a verdict may take to travel back.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(10);

    private readonly string root = Path.Combine(Path.GetTempPath(), "held-container-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleConstructionGate gate = new();
    private readonly PostCreationVerdictCapture verdicts = new();
    private string? modulePath;
    private string ModulePath => modulePath ??= Write(ModuleSource);

    public ACreateWaitsOnAModuleContainerUnderConstructionTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders(). The verdict is logged at Error, so no
        // level is changed anywhere.
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(verdicts));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s
                .AddSingleton(gate)
                .AddSingleton(new MeshOperationOptions { Timeout = Budget }))
            .InstallAssemblies(ModulePath);

    [Fact(Timeout = 120_000)]
    public async Task WhileAModuleSingletonIsBeingBuilt_AnUnrelatedCreate_IsAnsweredNamingTheModulesHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Module)?.Services
            .Should().NotBeNull("the module's services must live in a container of their own — the shape under test");
        gate.HandlersBuilt.Should().Be(0,
            "the precondition of a BOOT: nothing has asked the module's handler anything yet, so its "
            + "instance does not exist and the first create has to build it");

        // A module singleton under construction — what a module's hosted service is while the host
        // starts it. The construction parks INSIDE the module's container until released.
        gate.Arm();
        Task<CreateNodeResponse>? pending = null;
        string? verdict = null;
        var construction = new Thread(() =>
            Mesh.ServiceProvider.GetRequiredService<IModuleServiceUnderConstruction>().Touch())
        {
            IsBackground = true,
            Name = "module-singleton-under-construction",
        };
        construction.Start();
        try
        {
            SpinWait.SpinUntil(() => gate.Entered, TestTimeouts.Convergence)
                .Should().BeTrue("the module's slow singleton must be under construction before the create is issued");

            // Written as SYSTEM, as the stalled production creates were (the platform's boot
            // activity, its notifications): no partition bootstrap, so this is the only create.
            var path = $"{TestPartition}/unrelated-{Guid.NewGuid():N}";
            verdicts.Watch(path);
            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            pending = access.RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(Markdown(path))))
                .Select(d => d.Message)
                .FirstAsync().Await(ct);

            var logged = await verdicts.Verdict.Should().Within(Window).Emit(
                "a create whose post-creation leg waits must reach its verdict inside the operation budget",
                cancellationToken: ct);
            Output.WriteLine($"VERDICT while the container is held: {logged}");
            verdict = logged;
            logged.Should().Contain("post-creation-handlers: matching ",
                "the leg was asking a handler whether it applies — not running one");
            logged.Should().Contain($"[module {Module}, registration ",
                "the verdict names the module whose container the create is waiting on, WITHOUT resolving "
                + "the handler — 'its post-creation handlers' named every handler at once (#6391)");
            gate.HandlersBuilt.Should().Be(0,
                "the handler was never built: the create waited on the container, not on the handler");

            var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
            (await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().Await(ct))
                .Should().NotBeNull("the row was written before the post-creation leg began");
        }
        finally
        {
            gate.Release();
        }

        construction.Join(TestTimeouts.Convergence).Should().BeTrue("the released construction finishes");

        // The reply the deadline posted, delivered now that the hub's pump is free again: the SAME
        // verdict, never a late Ok for a create that was already answered.
        verdict.Should().NotBeNull();
        var response = await pending!.WaitAsync(Window, ct);
        Output.WriteLine($"CREATE answered: success={response.Success} reason={response.RejectionReason} error={response.Error}");
        response.Success.Should().BeFalse("the create was answered at its deadline, and is not answered twice");
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable);
        response.Error.Should().Contain("outcome is unknown");
        response.Error.Should().Contain("post-creation-handlers: matching ");
        response.Error.Should().Contain($"[module {Module}, registration ");
        verdicts.Errors.Should().ContainSingle(
            "ONE Error line for one create. The chain's own deadline expired while the leg was parked "
            + "inside its emission, and used to add 'reached no verdict — stalled at stage write' when the "
            + "leg returned: a second verdict, for a create already answered, naming a stage that had finished");

        // The other half: with the container free again, the same create shape is answered Ok.
        var after = $"{TestPartition}/unrelated-after-{Guid.NewGuid():N}";
        var healthy = await ObserveNodeOperation(new CreateNodeRequest(Markdown(after)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);
        healthy.Success.Should().BeTrue(healthy.Error ?? "nothing holds the module's container any more");
        gate.HandlersBuilt.Should().Be(1, "the module's handler is built once its container is free");
    }

    /// <summary>The negative control: nothing under construction, so the first create is answered Ok.</summary>
    [Fact(Timeout = 120_000)]
    public async Task WithNothingUnderConstruction_TheFirstCreate_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/unrelated-{Guid.NewGuid():N}";

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "no module container is held");
        gate.HandlersBuilt.Should().Be(1,
            "the create asked the module's handler whether it applies, which built it — the dependency "
            + "every create has on every module's container");
    }

    /// <summary>
    /// Emits the create handler's own "written, but its post-creation handlers had not finished"
    /// line for the watched path — the instrument production reads this failure through.
    /// </summary>
    private sealed class PostCreationVerdictCapture : ILoggerProvider
    {
        private readonly AsyncSubject<string> verdict = new();
        private ImmutableList<string> errors = [];
        private string? watched;

        internal IObservable<string> Verdict => verdict;

        /// <summary>Every Error line written about the watched path, in order.</summary>
        internal ImmutableList<string> Errors => errors;

        internal void Watch(string path) => Volatile.Write(ref watched, path);

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose() => verdict.Dispose();

        private void Offer(string message)
        {
            if (Volatile.Read(ref watched) is not { } path
                || !message.Contains(path, StringComparison.Ordinal))
                return;
            ImmutableInterlocked.Update(ref errors, e => e.Add(message));
            if (!message.Contains("its post-creation handlers had not finished", StringComparison.Ordinal))
                return;
            verdict.OnNext(message);
            verdict.OnCompleted();
        }

        private sealed class Capturing(PostCreationVerdictCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    owner.Offer(formatter(state, exception));
            }
        }
    }

    private static MeshNode Markdown(string path)
    {
        var separator = path.LastIndexOf('/');
        return new MeshNode(path[(separator + 1)..], path[..separator]) { Name = "Unrelated", NodeType = "Markdown" };
    }

    private const string ModuleSource = """
        #nullable enable
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.HeldContainer.Module]
        namespace MeshWeaver.Test.HeldContainer;
        public sealed class SeedHandler : MeshWeaver.Mesh.Services.INodePostCreationHandler
        {
            public string NodeType => "ATypeNoTestCreates";
            public System.IObservable<System.Reactive.Unit> Handle(MeshWeaver.Mesh.MeshNode createdNode, string? createdBy)
                => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        }
        public sealed class SlowToBuild : MeshWeaver.Graph.Test.IModuleServiceUnderConstruction
        {
            public SlowToBuild(MeshWeaver.Graph.Test.ModuleConstructionGate gate) => gate.Hold();
            public string Touch() => "built";
        }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("HeldContainerProbe") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        // A FACTORY registration, as the shipped modules write theirs: it names no type.
                        services.AddSingleton<MeshWeaver.Mesh.Services.INodePostCreationHandler>(sp =>
                        {
                            sp.GetRequiredService<MeshWeaver.Graph.Test.ModuleConstructionGate>().NoteHandlerBuilt();
                            return new SeedHandler();
                        });
                        services.AddSingleton<MeshWeaver.Graph.Test.IModuleServiceUnderConstruction, SlowToBuild>();
                        return services;
                    })];
        }
        """;

    private string Write(string source)
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IModuleServiceUnderConstruction).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
