using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Every node-type rule that NARROWS the permission fold — refuses a caller who holds Read — is
/// <see cref="INodeTypeExclusiveRead"/>, so a store-filtering listing (Postgres) never admits its rows
/// through a grant; and the predicates such a rule declares keep exactly its legitimate readers listing
/// (Doc/Architecture/AccessControl → "Rule read predicates").
///
/// <para>The rules: <c>StorageBindingAccessRule</c> (Read needs Update on the partition ROOT — a Viewer
/// is refused), the step-up <c>SystemOnlyAccessRule</c> (System alone), <c>VUserAccessRule</c> (portal hub
/// principals alone) and <see cref="GitHubSyncConfigAccessRule"/> (refuses Anonymous, which a public
/// Space projects Read to).</para>
///
/// <para>Each predicate is pinned by the contract predicate ⇒ HasAccess, over a caller who should list
/// (positive control) and callers who must not, with the narrowing itself asserted as a verdict first:
/// the Viewer HOLDS Read and is still refused on a point read.</para>
///
/// <para>🚨 <c>ConfigureMeshBase</c>, not <c>base.ConfigureMesh</c>: the latter grants Public the Admin
/// role everywhere, under which every negative below would pass proving nothing.</para>
/// </summary>
public class NarrowingRulesAreExclusiveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "nracme";
    private const string Viewer = "nr-viewer";
    private const string Owner = "nr-owner";
    private const string Outsider = "nr-outsider";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "Acme", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(Viewer, "Viewer", Partition),
                AssignmentNodeFactory.UserRole(Owner, "Admin", Partition))
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<ILoggerProvider>(_captured);
                return services;
            });

    private readonly CapturingLoggerProvider _captured = new();

    /// <summary>Records every Warning-or-above message of the mesh's loggers — one per test instance.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Warnings);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    sink.Enqueue(formatter(state, exception));
            }
        }
    }

    private NodeTypeAccessRuleSet Rules => Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>();

    private static readonly string[] NarrowingTypes =
    [
        StorageBindingPaths.NodeType,
        StepUpPaths.ReceiptNodeType, StepUpPaths.ConsumptionNodeType,
        StepUpPaths.PendingNodeType, StepUpPaths.FactorsNodeType,
        "VUser",
        GitHubSyncService.ConfigNodeType,
    ];

    /// <summary>The rules as this mesh registers them: every narrowing type is exclusive; an ordinary type is not.</summary>
    [Fact]
    public void EveryNarrowingRule_IsExclusive()
    {
        var exclusive = Rules.ExclusiveReadNodeTypes();
        exclusive.Should().Contain(NarrowingTypes,
            "a rule that refuses a Read-grant holder must cut its type out of the listing's grant arm");
        exclusive.Should().NotContain(["Markdown", "Space"], "control: types no narrowing rule governs follow the grant");
    }

    private async Task<Permission> Held(string path, string user)
        => await Mesh.GetEffectivePermissions(path, user).Should().Within(Budget)
            .Emit($"the fold for {user} on {path} must answer", cancellationToken: TestContext.Current.CancellationToken);

    private async Task<bool> PointRead(MeshNode node, string user)
    {
        var rule = Rules.Find(node.NodeType, NodeOperation.Read);
        rule.Should().NotBeNull($"{node.NodeType} must be governed by a rule");
        var outcome = await NodeTypeAccessRuleGate
            .Evaluate(rule!, new NodeValidationContext { Operation = NodeOperation.Read, Node = node }, user)
            .Should().Within(Budget).Emit("the rule must decide", cancellationToken: TestContext.Current.CancellationToken);
        return outcome.IsGranted;
    }

    /// <summary>Whether a listing that applies every subject would return <paramref name="node"/> to <paramref name="user"/>
    /// through the rule's predicates, with the permission fold read from the mesh.</summary>
    private async Task<bool> ListedByPredicate(MeshNode node, string user)
    {
        var bound = RuleReadPredicates.BindSupporting(Rules.ReadPredicates(), user, isGlobalAdmin: false, RuleReadSubject.All)
            .Where(p => p.NodeType == node.NodeType).ToArray();
        var targets = new[] { node.Path!, node.Path!.Split('/', 2)[0] }.Distinct();
        var held = new Dictionary<string, Permission>();
        foreach (var target in targets)
            held[target] = await Held(target, user);
        return bound.Any(p => p.Matches(node, null, Mesh.JsonSerializerOptions,
            (path, permission) => held.TryGetValue(path, out var h) && h.HasFlag(permission)));
    }

    /// <summary>
    /// 🚨 StorageBinding: a Viewer HOLDS Read on the partition and is refused a binding on a point read, and
    /// never matches the predicate; the partition's administrator (Update on the root) matches and is
    /// admitted — predicate ⇒ HasAccess for every caller.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task StorageBinding_PredicateListsTheAdministratorOnly()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var binding = new MeshNode("b1", StorageBindingPaths.NamespaceOf(Partition)) { NodeType = StorageBindingPaths.NodeType };

        (await Held(binding.Path!, Viewer)).HasFlag(Permission.Read).Should().BeTrue("the Viewer holds Read — the narrowing's premise");
        (await PointRead(binding, Viewer)).Should().BeFalse("the rule refuses a Viewer a binding");

        foreach (var user in new[] { Owner, Viewer, Outsider })
        {
            var listed = await ListedByPredicate(binding, user);
            if (listed)
                (await PointRead(binding, user)).Should().BeTrue($"predicate ⇒ HasAccess for {user}");
        }
        (await ListedByPredicate(binding, Owner)).Should().BeTrue("positive control: the partition administrator still lists bindings");
        (await ListedByPredicate(binding, Viewer)).Should().BeFalse("🚨 a Viewer never lists a binding");
        (await ListedByPredicate(binding, Outsider)).Should().BeFalse();

        RuleReadPredicates.Bind(Rules.ReadPredicates(), Owner, isGlobalAdmin: false)
            .Should().NotContain(p => p.NodeType == StorageBindingPaths.NodeType,
                "🚨 fail closed: a store that does not support the permission subject is never bound it (it would apply the type term alone)");
    }

    /// <summary>
    /// 🚨 A subject NOBODY knows is logged by name and opens nothing — through the baseline <c>Bind</c> as well
    /// as <c>BindSupporting</c> — while a subject this assembly knows but the consumer does not apply is skipped
    /// silently (the expected state of an older store). Control: the same call with only the known subject
    /// logs nothing.
    /// </summary>
    [Fact]
    public void AnUnknownSubject_IsLoggedByName_AKnownUnsupportedOneIsSilent()
    {
        RuleReadPredicate known = new()
        {
            NodeType = StorageBindingPaths.NodeType, Subject = RuleReadSubject.PermissionOnPartitionRoot,
            HeldPermission = Permission.Update,
        };
        RuleReadPredicate unknown = new() { NodeType = StorageBindingPaths.NodeType, Subject = "SomeModulesOwnSubject" };

        RuleReadPredicates.Bind([known], Owner, isGlobalAdmin: false, Mesh).Should().BeEmpty("a baseline consumer is never bound a permission subject");
        _captured.Warnings.Where(w => w.Contains(StorageBindingPaths.NodeType)).Should().BeEmpty(
            "control: a known subject the consumer does not apply is the expected state, not a warning");

        RuleReadPredicates.Bind([unknown], Owner, isGlobalAdmin: false, Mesh).Should().BeEmpty("an unknown subject opens nothing");
        RuleReadPredicates.BindSupporting([unknown], Owner, isGlobalAdmin: false, RuleReadSubject.All, Mesh).Should().BeEmpty();
        _captured.Warnings.Count(w => w.Contains("SomeModulesOwnSubject")).Should().Be(2,
            "🚨 an unknown subject is logged by name on every bind, or a module's new subject fails closed unnoticed");
    }

    /// <summary>
    /// 🚨 GitHubSyncConfig: an ordinary reader keeps listing a config they hold Read on, through the
    /// PermissionOnPath predicate (predicate ⇒ HasAccess); Anonymous — refused on a point read — is bound no
    /// predicate, and the type is exclusive, so a listing returns it nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GitHubSyncConfig_OrdinaryReadersStillList_AnonymousNever()
    {
        var config = new MeshNode("_GitSync", Partition) { NodeType = GitHubSyncService.ConfigNodeType };

        (await ListedByPredicate(config, Viewer)).Should().BeTrue("negative control: a Read holder still lists the config");
        (await PointRead(config, Viewer)).Should().BeTrue("predicate ⇒ HasAccess");
        (await ListedByPredicate(config, Outsider)).Should().BeFalse("no grant, no row");
        (await PointRead(config, WellKnownUsers.Anonymous)).Should().BeFalse("the rule refuses Anonymous");

        var anonymous = await RuleReadPredicates.ResolveScopeSupporting(Mesh, WellKnownUsers.Anonymous, RuleReadSubject.All)
            .Should().Within(Budget).Emit("resolution answers once", cancellationToken: TestContext.Current.CancellationToken);
        anonymous.Predicates.Should().BeEmpty("predicates are bound for real users only");
        anonymous.ExclusiveNodeTypes.Should().Contain(GitHubSyncService.ConfigNodeType,
            "🚨 so a grant Anonymous holds never lists a config");
    }
}
