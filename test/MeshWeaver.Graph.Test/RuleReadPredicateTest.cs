using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Layout;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A node type's access rule that grants Read beyond the permission fold — the shape of the feedback
/// submission rule (a global admin and the record's own submitter) — DECLARES the rows it opens as
/// <see cref="RuleReadPredicate"/>s, so a LISTING returns exactly what a POINT READ admits
/// (Doc/Architecture/AccessControl → "Rule read predicates").
///
/// <para>Measured on the in-memory executor, which runs the rule per row; the Postgres executor, which
/// applies the predicates in SQL, is pinned by the same three cases in MeshWeaver.Plugins. The property
/// test pins the contract every declaring rule owes: predicate ⇒ HasAccess.</para>
/// </summary>
public class RuleReadPredicateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RecordType = "Inbox/Record";
    private const string Partition = "Inbox";
    private const string Prefix = "Inbox/_Sub/";

    private static readonly AccessContext Erin = new() { ObjectId = "erin", Name = "erin" };   // global admin
    private static readonly AccessContext Carol = new() { ObjectId = "carol", Name = "carol" }; // a submitter
    private static readonly AccessContext Dave = new() { ObjectId = "dave", Name = "dave" };    // neither

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(RecordContent), nameof(RecordContent))
        .AddMeshNodes(
            new MeshNode("Record", "Inbox")
            {
                NodeType = "NodeType",
                Name = RecordType,
                Content = new NodeTypeDefinition(),
                HubConfiguration = config => config.WithContentType<RecordContent>().AddDefaultLayoutAreas(),
            },
            new MeshNode(PermissionEvaluator.AdminScope) { NodeType = "Markdown", Name = "Admin" },
            AssignmentNodeFactory.UserRole(Erin.ObjectId, "Admin", PermissionEvaluator.AdminScope))
        .ConfigureServices(services => services
            .AddSingleton<INodeTypeAccessRule>(sp => new SubmissionRule(sp.GetRequiredService<IMessageHub>())));

    /// <summary>A submission record: who filed it, and what it says.</summary>
    /// <param name="SubmittedBy">The submitter's user id.</param>
    /// <param name="Message">The record's text.</param>
    public sealed record RecordContent(string SubmittedBy, string Message);

    /// <summary>The feedback-submission shape: the fold, else Read below <see cref="Prefix"/> for the
    /// record's own submitter and for a global admin.</summary>
    private sealed class SubmissionRule(IMessageHub hub) : INodeTypeAccessRule, INodeTypeReadPredicates
    {
        public string NodeType => RecordType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];

        public IReadOnlyList<RuleReadPredicate> ReadPredicates =>
        [
            new() { NodeType = RecordType, PathPrefix = Prefix, Subject = RuleReadSubject.GlobalAdmin },
            new() { NodeType = RecordType, PathPrefix = Prefix, Subject = RuleReadSubject.ContentFieldIsCaller, CallerField = "submittedBy" },
        ];

        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId)
        {
            var user = userId ?? WellKnownUsers.Anonymous;
            var path = context.Node.Path ?? "";
            return hub.CheckPermission(path, user, Permission.Read).Take(1)
                .SelectMany(granted => granted || !path.StartsWith(Prefix, StringComparison.Ordinal) || path.Length == Prefix.Length
                    ? Observable.Return(granted)
                    : NodeTypeAccessRuleGate.ReadSubjectNode(hub, path).Take(1)
                        .SelectMany(stored => SubmitterOf(stored) == user
                            ? Observable.Return(true)
                            : hub.IsGlobalAdmin(user).Take(1)));
        }

        private string? SubmitterOf(MeshNode? node)
        {
            if (node?.Content is null)
                return null;
            var json = System.Text.Json.JsonSerializer.SerializeToElement(node.Content, node.Content.GetType(), hub.JsonSerializerOptions);
            return json.ValueKind == System.Text.Json.JsonValueKind.Object
                   && json.TryGetProperty("submittedBy", out var v)
                   && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
    }

    private async Task<(string Carols, string Others)> Seed(CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = Partition });
        await AsSystem(new MeshNode("_Sub", Partition) { NodeType = "Markdown", Name = "submissions" }, ct);
        var carols = (await AsSystem(Record("a", Carol.ObjectId), ct)).Path;
        var others = (await AsSystem(Record("b", "frank"), ct)).Path;
        return (carols, others);
    }

    private static MeshNode Record(string id, string submitter) => new(id, Prefix.TrimEnd('/'))
    {
        NodeType = RecordType,
        Name = id,
        Content = new RecordContent(submitter, "m"),
    };

    /// <summary>
    /// 🚨 A global admin LISTS every submission and the submitter LISTS their own — exactly the rows
    /// each can POINT-read. NEGATIVE CONTROL: a user who is neither lists nothing and point-reads nothing.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AListing_ReturnsWhatTheRuleAdmitsOnAPointRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (carols, others) = await Seed(ct);
        var query = $"namespace:{Prefix.TrimEnd('/')}";

        Assert.True(await PointRead(Erin, carols, ct), "the admin point-reads a submission");
        Assert.Equal([carols, others], (await PathsAs(Erin, query, ct)).Order().ToArray());

        Assert.True(await PointRead(Carol, carols, ct), "the submitter point-reads her own");
        Assert.False(await PointRead(Carol, others, ct), "…and not somebody else's");
        Assert.Equal([carols], await PathsAs(Carol, query, ct));

        Assert.False(await PointRead(Dave, carols, ct), "NEGATIVE CONTROL: neither point-reads it");
        Assert.Empty(await PathsAs(Dave, query, ct));
    }

    /// <summary>
    /// The rule set returns the GOVERNING rule's predicates, resolved per caller: both for the admin,
    /// the submitter one bound to the caller for anyone else, nothing for a caller who is not a real user.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Predicates_ResolvePerCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var declared = Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>().ReadPredicates();
        Assert.Equal(2, declared.Count(p => p.NodeType == RecordType));
        Assert.Equal(Partition, declared.First(p => p.NodeType == RecordType).OpenedPartition);

        var forErin = await Resolve(Erin.ObjectId, ct);
        Assert.Contains(forErin, p => p.CallerField is null && p.NodeType == RecordType);
        var forDave = await Resolve(Dave.ObjectId, ct);
        Assert.DoesNotContain(forDave, p => p.CallerField is null);
        Assert.Contains(forDave, p => p is { CallerField: "submittedBy", CallerId: "dave" });
        Assert.Empty(await Resolve(WellKnownUsers.Anonymous, ct));
        Assert.Empty(await Resolve(WellKnownUsers.System, ct));
    }

    /// <summary>
    /// 🚨 THE CONTRACT — predicate ⇒ HasAccess. For every caller, every predicate resolved for them and
    /// every seeded row (and a row OUTSIDE the prefix): wherever the predicate matches, the rule grants
    /// the point read. And the predicates are not vacuous: each caller who may read a row has one that
    /// matches it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task EveryPredicateMatch_IsAdmittedByTheRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var (carols, others) = await Seed(ct);
        var outside = (await AsSystem(new MeshNode("c", Partition)
        {
            NodeType = RecordType, Name = "c", Content = new RecordContent(Carol.ObjectId, "m"),
        }, ct)).Path;
        var matched = 0;
        foreach (var caller in new[] { Erin, Carol, Dave })
        {
            var resolved = await Resolve(caller.ObjectId, ct);
            foreach (var path in new[] { carols, others, outside })
            {
                var stored = await Mesh.ServiceProvider.GetRequiredService<AccessService>()
                    .RunAsSystem(() => Mesh.GetMeshNodeStream(path)).OfType<MeshNode>().Take(1)
                    .Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);
                foreach (var predicate in resolved.Where(p => p.Matches(stored, "Markdown", Mesh.JsonSerializerOptions)))
                {
                    matched++;
                    Assert.True(await PointRead(caller, path, ct),
                        $"predicate {predicate} matched {path} for {caller.ObjectId}, so the rule must admit the point read");
                }
            }
        }
        // erin: both submissions; carol: her own. Never the row outside the prefix.
        Assert.Equal(3, matched);
    }

    private Task<ImmutableArray<ResolvedReadPredicate>> Resolve(string? userId, CancellationToken ct)
        => RuleReadPredicates.Resolve(Mesh, userId).Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);

    private Task<bool> PointRead(AccessContext caller, string path, CancellationToken ct)
    {
        var slash = path.LastIndexOf('/');
        var validator = Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();
        return validator.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Read,
                Node = new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = RecordType },
                AccessContext = caller,
                IsPointRead = true,
            })
            .Take(1)
            .Select(r => r.IsValid)
            .Should().Within(TimeSpan.FromSeconds(10)).Emit(cancellationToken: ct);
    }

    private Task<string[]> PathsAs(AccessContext caller, string query, CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAs(caller, () =>
                NodeFactory.Query<MeshNode>(MeshQueryRequest.FromQuery($"{query} select:path")))
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => (change.Items ?? []).Select(n => n.Path).OfType<string>().Where(p => p.Length > 0).ToArray())
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(cancellationToken: ct);

    private Task<MeshNode> AsSystem(MeshNode node, CancellationToken ct)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateNode(node)).OfType<MeshNode>().Should().Emit(cancellationToken: ct);

    /// <summary>
    /// 🚨 FAIL CLOSED — a subject no resolver knows opens NOTHING, for every caller, a global admin
    /// included; it is never read as a default subject. Pure: <see cref="RuleReadPredicates.Bind"/>.
    /// </summary>
    [Fact]
    public void AnUnknownSubject_OpensNothing_ForAnyCaller()
    {
        RuleReadPredicate[] declared =
        [
            new() { NodeType = RecordType, PathPrefix = Prefix, Subject = "SomeModulesOwnSubject" },
            new() { NodeType = RecordType, PathPrefix = Prefix, Subject = RuleReadSubject.ContentFieldIsCaller },
        ];
        Assert.Empty(RuleReadPredicates.Bind(declared, Erin.ObjectId, isGlobalAdmin: true));
        Assert.Empty(RuleReadPredicates.Bind(declared, Dave.ObjectId, isGlobalAdmin: false));
    }

    /// <summary>
    /// 🚨 FAIL CLOSED — excluded partition-root types: a row whose partition root is excluded does not
    /// match, nor does a row whose root is ABSENT; a row under any other root does. Pure:
    /// <see cref="ResolvedReadPredicate.Matches"/>.
    /// </summary>
    [Fact]
    public void ExcludedPartitionRootTypes_CloseAPersonsHome_AndAnAbsentRoot()
    {
        var predicate = Assert.Single(RuleReadPredicates.Bind(
            [new() { NodeType = RecordType, Subject = RuleReadSubject.GlobalAdmin, ExcludedPartitionRootTypes = ["User"] }],
            Erin.ObjectId, isGlobalAdmin: true));
        var row = new MeshNode("r", "pkg/_Thread") { NodeType = RecordType, Content = new RecordContent("x", "m") };
        var options = Mesh.JsonSerializerOptions;
        Assert.True(predicate.Matches(row, "Space", options), "a row under another root type matches");
        Assert.False(predicate.Matches(row, "User", options), "a row in a person's home does not");
        Assert.False(predicate.Matches(row, null, options), "a row whose partition root is absent does not");
        Assert.False(predicate.Matches(row with { NodeType = "Other" }, "Space", options), "another node type never matches");
    }
}
