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
/// The switches the test holds a module's code with, and the journal of what the module's container
/// built. A platform singleton; instance state only.
/// </summary>
public sealed class ModuleConstructionGate
{
    private int matchingArmed;
    private int constructionArmed;
    private int entered;
    private int released;
    private int handlersBuilt;

    /// <summary>How long a parked call waits for its release before giving up by itself.</summary>
    public static readonly TimeSpan ParkBound = TimeSpan.FromSeconds(45);

    /// <summary>From now on the module handler's <c>Matches</c> parks until <see cref="Release"/>.</summary>
    public void ArmMatching() => Volatile.Write(ref matchingArmed, 1);

    /// <summary>From now on the slow singleton's CONSTRUCTION parks until <see cref="Release"/>.</summary>
    public void ArmConstruction() => Volatile.Write(ref constructionArmed, 1);

    /// <summary>Whether a call is parked inside the module right now.</summary>
    public bool Entered => Volatile.Read(ref entered) == 1;

    /// <summary>How many times a module container built its post-creation handler.</summary>
    public int HandlersBuilt => Volatile.Read(ref handlersBuilt);

    /// <summary>Lets the parked call finish.</summary>
    public void Release() => Volatile.Write(ref released, 1);

    /// <summary>Called by the module from INSIDE its handler's <c>Matches</c>.</summary>
    public void HoldMatching() => Hold(ref matchingArmed);

    /// <summary>Called by the module from INSIDE its slow singleton's constructor.</summary>
    public void HoldConstruction() => Hold(ref constructionArmed);

    private void Hold(ref int armed)
    {
        if (Volatile.Read(ref armed) == 0)
            return;
        Volatile.Write(ref entered, 1);
        SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, ParkBound);
    }

    /// <summary>Called by the module's handler registration when a container builds the handler.</summary>
    public void NoteHandlerBuilt() => Interlocked.Increment(ref handlersBuilt);
}

/// <summary>
/// MeshWeaver#6391 and #6542 — <b>a create never waits on a module's container, and when it waits on
/// a module's handler the verdict names that handler.</b>
///
/// <para>A post-creation handler a module registers reaches the mesh as a forwarding proxy
/// (<see cref="ModuleServiceProxy"/>), and every call on the proxy resolves the module's instance from
/// the module's OWN container. Autofac builds a container's shared instances one at a time, under one
/// lock, so while ANY singleton of that module was under construction the handler could not be built,
/// and a create that only wanted to learn "this handler does not apply to me" waited — the boot-time
/// stalls of #6391 (18 of 18 on a fresh pod, all System-written creates no handler applies to).</para>
///
/// <para>Since #6542 every shared instance of a module's container is <b>built before the module is
/// published</b> — at boot before the root provider is handed out, and on a swap before it commits — so
/// the proxy only ever reads a built handler, which Autofac answers without the lock.
/// <see cref="WhileANewGenerationsSingletonIsBeingBuilt_AnUnrelatedCreate_IsAnsweredOk"/> pins it: a
/// singleton of the NEXT generation parks inside its constructor, and a create is answered <c>Ok</c>
/// meanwhile. Before #6542 this test held the construction of a published module's singleton and pinned
/// the wait; that state no longer exists.</para>
///
/// <para>The naming half of #6391 still stands for a handler that is itself slow:
/// <see cref="WhileAModuleHandlerIsParked_AnUnrelatedCreate_IsAnsweredNamingTheModulesHandler"/> parks the
/// module handler's <c>Matches</c> and pins what the verdict says: the row is written, and the leg is
/// waiting at <c>matching</c> the module's handler, named by module and registration.</para>
///
/// <para><b>Why the verdict is read off the log first.</b> The in-memory store completes its write
/// on the node-operation hub's own turn, so the parked <c>Matches</c> parks that hub's pump with
/// it, and the reply the deadline posts stays queued behind it until the release. (On a database
/// the write completes on an I/O thread, the pump stays idle and the reply travels at once — which
/// is what the production trail shows.) The log line is written by the deadline's own thread, so
/// it is the signal that exists while the handler is still held; the reply is then read after
/// the release and must carry the same verdict.</para>
/// </summary>
public sealed class ACreateNeverWaitsOnAModuleContainerUnderConstructionTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.HeldContainer";

    /// <summary>Rung 1 of the ladder for this mesh — the create's verdict deadline.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>How long past the budget a verdict may take to travel back.</summary>
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(10);

    /// <summary>How long a live swap may take once its parked construction is released.</summary>
    private static readonly TimeSpan SwapBudget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "held-container-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleConstructionGate gate = new();
    private readonly PostCreationVerdictCapture verdicts = new();
    private string? modulePath;
    private string ModulePath => modulePath ??= Write("g1");

    public ACreateNeverWaitsOnAModuleContainerUnderConstructionTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders(). The verdict is logged at Error, so no
        // level is changed anywhere.
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(verdicts));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s
                .AddSingleton(gate)
                .AddSingleton(new MeshOperationOptions { Timeout = Budget }))
            .InstallAssemblies(ModulePath);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

    [Fact(Timeout = 180_000)]
    public async Task WhileANewGenerationsSingletonIsBeingBuilt_AnUnrelatedCreate_IsAnsweredOk()
    {
        var ct = TestContext.Current.CancellationToken;
        Contexts.Current(Module)?.Services.Should().NotBeNull(
            "the module's services must live in a container of their own — the shape under test");
        gate.HandlersBuilt.Should().Be(1, "the module's handler was built at boot, before the module was published");
        var serving = Contexts.Current(Module)!.Location;

        // N+1's slow singleton parks INSIDE its constructor while the swap builds N+1's container —
        // a module constructor that takes its time, the shape that stalled every first-time root
        // resolve when module constructors ran under the root's lock (the rejected fix of #6542).
        gate.ArmConstruction();
        var updater = Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Graph.Configuration.ModuleLiveUpdater>();
        var swap = updater.Swap(Write("g2"), "test: a slow constructor in the next generation").Await(ct);
        try
        {
            SpinWait.SpinUntil(() => gate.Entered, TestTimeouts.Convergence)
                .Should().BeTrue("N+1's slow singleton must be under construction before the create is issued");
            Contexts.Current(Module)!.Location.Should().Be(serving,
                "N+1 is being built and is not published: N serves");

            var path = $"{TestPartition}/unrelated-{Guid.NewGuid():N}";
            verdicts.Watch(path);
            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            var response = await access.RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(Markdown(path))))
                .Select(d => d.Message)
                .Should().Within(Window).Emit(
                    "a create asks N's built handler, which waits for nothing N+1's container is doing",
                    cancellationToken: ct);
            response.Success.Should().BeTrue(response.Error ?? "N's handler is built");
            verdicts.Errors.Should().BeEmpty("nothing waited");
            gate.HandlersBuilt.Should().Be(2,
                "N+1's handler was built before its slow singleton — registration order — and N's was not built again");
        }
        finally
        {
            gate.Release();
        }

        var outcome = await swap.WaitAsync(SwapBudget, ct);
        outcome.Kind.Should().Be(MeshWeaver.Graph.Configuration.ModuleSwapKind.Live, outcome.Reason);
        Contexts.Current(Module)!.Location.Should().NotBe(serving, "once built, N+1 is published");
    }

    [Fact(Timeout = 120_000)]
    public async Task WhileAModuleHandlerIsParked_AnUnrelatedCreate_IsAnsweredNamingTheModulesHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        gate.HandlersBuilt.Should().Be(1, "the module's handler was built at boot, before the module was published");

        gate.ArmMatching();
        Task<CreateNodeResponse>? pending = null;
        string? verdict = null;
        try
        {
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
            Output.WriteLine($"VERDICT while the handler is held: {logged}");
            verdict = logged;
            gate.Entered.Should().BeTrue("the leg is parked inside the module handler's Matches");
            logged.Should().Contain("post-creation-handlers: matching ",
                "the leg was asking a handler whether it applies — not running one");
            logged.Should().Contain($"[module {Module}, registration ",
                "the verdict names the module handler the create is waiting on, WITHOUT resolving it — "
                + "'its post-creation handlers' named every handler at once (#6391)");

            var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
            (await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().Await(ct))
                .Should().NotBeNull("the row was written before the post-creation leg began");
        }
        finally
        {
            gate.Release();
        }

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

        // The other half: with the handler free again, the same create shape is answered Ok.
        var after = $"{TestPartition}/unrelated-after-{Guid.NewGuid():N}";
        var healthy = await ObserveNodeOperation(new CreateNodeRequest(Markdown(after)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);
        healthy.Success.Should().BeTrue(healthy.Error ?? "nothing holds the module's handler any more");
        gate.HandlersBuilt.Should().Be(1, "the handler was built once, at boot");
    }

    /// <summary>The negative control: nothing held, so the first create is answered Ok.</summary>
    [Fact(Timeout = 120_000)]
    public async Task WithNothingHeld_TheFirstCreate_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/unrelated-{Guid.NewGuid():N}";

        var response = await ObserveNodeOperation(new CreateNodeRequest(Markdown(path)))
            .Select(d => d.Message)
            .Should().Within(Window).Emit(cancellationToken: ct);

        response.Success.Should().BeTrue(response.Error ?? "nothing is held");
        gate.HandlersBuilt.Should().Be(1,
            "the module's handler was built at boot, before the module was published — the first create "
            + "only read it");
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
        public sealed class SeedHandler(MeshWeaver.Graph.Test.ModuleConstructionGate gate) : MeshWeaver.Mesh.Services.INodePostCreationHandler
        {
            public string NodeType => "ATypeNoTestCreates";
            public bool Matches(MeshWeaver.Mesh.MeshNode createdNode)
            {
                gate.HoldMatching();
                return false;
            }
            public System.IObservable<System.Reactive.Unit> Handle(MeshWeaver.Mesh.MeshNode createdNode, string? createdBy)
                => System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
        }
        public sealed class SlowToBuild : MeshWeaver.Graph.Test.IModuleServiceUnderConstruction
        {
            public SlowToBuild(MeshWeaver.Graph.Test.ModuleConstructionGate gate) => gate.HoldConstruction();
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
                            var gate = sp.GetRequiredService<MeshWeaver.Graph.Test.ModuleConstructionGate>();
                            gate.NoteHandlerBuilt();
                            return new SeedHandler(gate);
                        });
                        services.AddSingleton<MeshWeaver.Graph.Test.IModuleServiceUnderConstruction, SlowToBuild>();
                        return services;
                    })];
        }
        """;

    private string Write(string generation)
    {
        var source = ModuleSource;
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(IModuleServiceUnderConstruction).Assembly.Location));
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
