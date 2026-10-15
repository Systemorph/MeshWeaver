using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The test's CONTROLLED SWITCH: a platform singleton the test module's rule calls at a named point
/// (<c>metadata</c> — inside its <c>SupportedOperations</c>; <c>evaluate</c> — inside its
/// <c>HasAccess</c>). Armed once, it runs the test's action exactly there, so a retirement lands
/// between two steps deterministically. It also journals the disposal of the module's singleton, so a
/// test can see WHEN the generation's services were disposed without reading module internals.
/// </summary>
public sealed class RetireSwitch
{
    private sealed record Arming(string Point, Action Act);

    private Arming? armed;
    private ImmutableList<string> journal = [];

    /// <summary>Everything written, in order.</summary>
    public ImmutableList<string> Journal => journal;

    /// <summary>Runs <paramref name="act"/> the next time <paramref name="point"/> is reached.</summary>
    public void Arm(string point, Action act) => Volatile.Write(ref armed, new Arming(point, act));

    /// <summary>Called by the module at <paramref name="point"/>; runs the armed action once.</summary>
    public void Fire(string point)
    {
        var arming = Volatile.Read(ref armed);
        if (arming is not null && arming.Point == point
            && ReferenceEquals(Interlocked.CompareExchange(ref armed, null, arming), arming))
            arming.Act();
    }

    /// <summary>Appends one journal entry.</summary>
    public void Write(string entry) => ImmutableInterlocked.Update(ref journal, j => j.Add(entry));
}

/// <summary>
/// 🚨 MeshWeaver#6715 — a module's forwarded service is used under an ATOMIC LEASE, never "is its module
/// still current?" followed by a call through the proxy. <c>NodeTypeAccessRuleSet</c> asked
/// <c>ModuleServiceProxy.Serves</c> and then called <c>SupportedOperations</c> / <c>HasAccess</c> on the
/// proxy; <c>ModuleContexts.Retire</c> could dispose the generation in between, and a write concurrent
/// with an uninstall failed with <c>Module … holds no service scope</c>.
///
/// <para>The module here registers an <see cref="INodeTypeAccessRule"/> (forwarded to the root as a
/// proxy) over a module-owned singleton (<c>Fence</c>) that the generation's container disposes. The
/// rule calls <see cref="RetireSwitch"/> at the step under test, and the test's armed action retires
/// the module's generation right there — the race, made deterministic.</para>
/// </summary>
public sealed class ModuleRuleLeasedAcrossRetirementTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LeasedRule";
    private const string ProbeType = "LeasedRuleProbe";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private readonly string root = Path.Combine(Path.GetTempPath(), "leased-rule-" + Guid.NewGuid().ToString("N"));

    public ModuleRuleLeasedAcrossRetirementTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<RetireSwitch>())
            .InstallAssemblies(Write());

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();
    private RetireSwitch Switch => Mesh.ServiceProvider.GetRequiredService<RetireSwitch>();

    private INodeTypeAccessRule ModuleRule() =>
        Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>()
            .Single(r => ModuleServiceProxy.Label(r).StartsWith("GuardedRule", StringComparison.Ordinal));

    private ModuleGeneration Generation() =>
        Contexts.Current(Module) ?? throw new Xunit.Sdk.XunitException($"{Module} is not held by the module registry");

    private static NodeValidationContext ReadOf() => new()
    {
        Operation = NodeOperation.Read,
        Node = new MeshNode("probe-node") { NodeType = ProbeType },
    };

    /// <summary>
    /// The lease itself: a retirement that STARTS while a module service is leased is deferred to the
    /// release — the leased instance still answers on live services, no new lease is granted, and the
    /// generation's singletons are disposed (and its context unloaded) only once the lease is released.
    /// Negative control: <c>ModuleServices.Dispose</c> disposing at once (the base) — the leased call
    /// then faults with the fence's <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetirementThatStartsUnderALease_IsDeferredToTheRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = ModuleRule();
        var generation = Generation();
        var retired = new ReplaySubject<bool>();

        using (var lease = ModuleServiceProxy.TryLease(rule))
        {
            lease.Should().NotBeNull("the module is current");
            Contexts.Retire(generation, Budget).Subscribe(retired); // the switch: retire under the lease

            ModuleServiceProxy.TryLease(rule).Should().BeNull("a retiring generation grants no new lease");
            Switch.Journal.Should().NotContain("disposed", "the generation's services must outlive the lease");
            (await lease!.Service.HasAccess(ReadOf(), "user").Timeout(Budget).Await(ct)).Should().BeTrue(
                "the leased instance answers on its own, still live, services");
            lease.Service.SupportedOperations.Should().BeEmpty();
        }

        Switch.Journal.Should().Equal(["disposed"], "the last lease out disposes the generation's services");
        (await retired.Timeout(Budget).Await(ct)).Should().BeTrue("the retirement unloads once nothing leases it");
    }

    /// <summary>
    /// The evaluation is pinned: the retirement fires INSIDE the rule's <c>HasAccess</c> (an uninstall
    /// landing while a write is being decided), and the decision still completes on the generation it
    /// started with. Negative control: the base, where the rule set hands out the bare proxy — the
    /// generation is disposed under the evaluation and the verdict is Undetermined
    /// (<see cref="ObjectDisposedException"/>).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetirementDuringTheEvaluation_DoesNotPullTheGenerationFromUnderIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var set = new NodeTypeAccessRuleSet(Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>());
        var retired = new ReplaySubject<bool>();
        var generation = Generation();
        var rule = set.Find(ProbeType, NodeOperation.Read);
        rule.Should().NotBeNull("the module's rule governs its type");

        Switch.Arm("evaluate", () => Contexts.Retire(generation, Budget).Subscribe(retired));
        var outcome = await NodeTypeAccessRuleGate.Evaluate(rule!, ReadOf(), "user").Timeout(Budget).Await(ct);

        outcome.IsGranted.Should().BeTrue(outcome.UndeterminedReason ?? "denied");
        // The unload follows the release of the evaluation's lease, and the disposal precedes the
        // unload — so once the retirement has emitted, the services were disposed after the verdict.
        (await retired.Timeout(Budget).Await(ct)).Should().BeTrue();
        Switch.Journal.Should().Equal(["disposed"], "disposed once the evaluation released its lease");
        set.Find(ProbeType, NodeOperation.Read).Should().BeNull("a retired module's rule leaves the set");
    }

    /// <summary>
    /// The metadata read is atomic, and a retirement BETWEEN selection and evaluation is a named,
    /// fail-closed verdict. The retirement fires inside <c>SupportedOperations</c>, which
    /// <see cref="NodeTypeAccessRuleSet.Find"/> reads under its lease: the read completes on live
    /// services, the rule is selected, and its evaluation — after the generation is gone — is
    /// Undetermined naming <see cref="ModuleRetiredException"/>, never <c>holds no service scope</c>.
    /// Negative control: the base, where <c>Find</c> checks <c>Serves</c> and then reads through the
    /// proxy — the generation is disposed under the read and <c>Find</c> itself throws.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetirementDuringSelection_IsANamedFailClosedVerdict_NeverAFaultInFind()
    {
        var ct = TestContext.Current.CancellationToken;
        var set = new NodeTypeAccessRuleSet(Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>());
        var retired = new ReplaySubject<bool>();
        var generation = Generation();

        Switch.Arm("metadata", () => Contexts.Retire(generation, Budget).Subscribe(retired));
        var rule = set.Find(ProbeType, NodeOperation.Read);

        rule.Should().NotBeNull("the operations were read on live services under the lease");
        Switch.Journal.Should().Equal(["disposed"], "the selection's lease was released, so the retirement proceeded");
        var outcome = await NodeTypeAccessRuleGate.Evaluate(rule!, ReadOf(), "user").Timeout(Budget).Await(ct);
        outcome.IsUndetermined.Should().BeTrue("a rule whose module left before it was evaluated cannot decide — fail closed");
        outcome.UndeterminedReason.Should().Contain(nameof(ModuleRetiredException));
        (await retired.Timeout(Budget).Await(ct)).Should().BeTrue();
        set.Find(ProbeType, NodeOperation.Read).Should().BeNull("asking again decides without the retired rule");
    }

    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Reactive.Linq;
        using MeshWeaver.Mesh;
        using MeshWeaver.Mesh.Security;
        using MeshWeaver.Mesh.Services;
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.LeasedRule.Module]
        namespace MeshWeaver.Test.LeasedRule;
        public sealed class Fence(MeshWeaver.Graph.Test.RetireSwitch retire) : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; retire.Write("disposed"); }
        }
        public sealed class GuardedRule(Fence fence, MeshWeaver.Graph.Test.RetireSwitch retire) : INodeTypeAccessRule
        {
            public string NodeType => "LeasedRuleProbe";
            public IReadOnlyCollection<NodeOperation> SupportedOperations
            {
                get
                {
                    retire.Fire("metadata");
                    return fence.Disposed
                        ? throw new ObjectDisposedException(nameof(Fence), "read under a disposed generation")
                        : Array.Empty<NodeOperation>();
                }
            }
            public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
            {
                retire.Fire("evaluate");
                return Observable.Defer(() => fence.Disposed
                    ? Observable.Throw<bool>(new ObjectDisposedException(nameof(Fence), "evaluated under a disposed generation"))
                    : Observable.Return(true));
            }
        }
        public sealed class ModuleAttribute : MeshNodeProviderAttribute
        {
            public override IEnumerable<MeshNode> Nodes =>
                [new MeshNode("LeasedRuleProbeNode") { Name = "probe", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        services.AddSingleton<Fence>();
                        services.AddSingleton<INodeTypeAccessRule, GuardedRule>();
                        return services;
                    })];
        }
        """;

    private string Write()
    {
        var references = PlatformReferences.Platform()
            .Add(MetadataReference.CreateFromFile(typeof(RetireSwitch).Assembly.Location));
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
