using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Graph.Security;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The pure half of the grant-subjects query (policy <c>user-listing-needs-grant-right</c>, #7908):
/// the scope a picker's query names, and the directory read it becomes.
/// </summary>
public class GrantSubjectsQueryPureTest
{
    [Theory]
    [InlineData("Auth/_GrantSubjects/acme/Docs", "acme/Docs")]
    [InlineData("Auth/_GrantSubjects/acme/_Access", "acme")]
    [InlineData("Auth/_GrantSubjects/acme", "acme")]
    [InlineData("Auth/_GrantSubjects", "")]
    [InlineData("Auth/_GrantSubjects/", "")]
    public void TheScope_IsReadFromTheReservedNamespace(string ns, string scope)
        => AccessSubjectQueries.GrantScopeOf(ns).Should().Be(scope);

    [Theory]
    [InlineData("acme/Docs")]
    [InlineData("")]
    [InlineData("Auth")]
    [InlineData("Auth/_GrantSubjects/{node.namespace}")]
    public void AnythingElse_IsNoGrantSubjectsNamespace(string ns)
        => AccessSubjectQueries.GrantScopeOf(ns).Should().BeNull();

    [Fact]
    public void ThePickersUsersHalf_IsTheGatedQuery_NeverTheRawDirectoryRead()
    {
        AccessSubjectQueries.ForScope("acme/Docs")[0].Should().Be("namespace:\"Auth/_GrantSubjects/acme/Docs\" nodeType:User");
        AccessSubjectQueries.ForScope("acme/Docs").Should().NotContain(AccessSubjectQueries.Users);
        AccessSubjectQueries.ForScope(null)[0].Should().Be("namespace:\"Auth/_GrantSubjects\" nodeType:User");
        MeshWeaver.Domain.MeshNodeAttribute.ResolveQueries([AccessSubjectQueries.UsersTemplate], "acme/_Access")[0]
            .Should().Be("namespace:\"Auth/_GrantSubjects/acme/_Access\" nodeType:User");
    }

    /// <summary>A scope with whitespace or a quote stays ONE value, so the right is checked at the
    /// scope the picker grants on — never at a truncated prefix of it.</summary>
    [Theory]
    [InlineData("acme/Shared Documents")]
    [InlineData("acme/Bob's Team")]
    [InlineData("acme/The \"Q\" Team")]
    public void TheScope_RoundTripsThroughTheQuery(string scope)
    {
        var parsed = new QueryParser().Parse(AccessSubjectQueries.UsersAt(scope));
        AccessSubjectQueries.GrantScopeOf(parsed.Path).Should().Be(scope);
        parsed.ExtractNodeType().Should().Be("User");

        var template = MeshWeaver.Domain.MeshNodeAttribute.ResolveQueries(
            [AccessSubjectQueries.UsersTemplate], "acme/Shared Documents/_Access")[0];
        AccessSubjectQueries.GrantScopeOf(new QueryParser().Parse(template).Path)
            .Should().Be("acme/Shared Documents");
    }

    [Fact]
    public void TheDirectoryRead_KeepsTheCallersTermsAndDropsTheLocation()
    {
        GrantSubjectsQueryProvider.DirectoryQuery("namespace:Auth/_GrantSubjects/acme nodeType:User")
            .Should().Be(AccessSubjectQueries.Users);
        GrantSubjectsQueryProvider.DirectoryQuery("namespace:Auth/_GrantSubjects/acme nodeType:User scope:descendants burgi sort:name")
            .Should().Be($"{AccessSubjectQueries.Users} burgi sort:name");
        GrantSubjectsQueryProvider.DirectoryQuery("namespace:Auth/_GrantSubjects/acme nodeType:User name:\"Max Muster\"")
            .Should().Be($"{AccessSubjectQueries.Users} name:\"Max Muster\"");
        // Cut by the parser's own tokenizer: tabs and single-quoted values parse as they do anywhere.
        GrantSubjectsQueryProvider.DirectoryQuery("namespace:Auth/_GrantSubjects/acme\tnodeType:User\tname:Max")
            .Should().Be($"{AccessSubjectQueries.Users} name:Max", "the name filter survives a tab-delimited query");
        var quoted = GrantSubjectsQueryProvider.DirectoryQuery(
            "namespace:'Auth/_GrantSubjects/acme/Shared Documents' nodeType:User");
        quoted.Should().Be(AccessSubjectQueries.Users, "no stray text is left from a single-quoted namespace");
    }
}

/// <summary>
/// The grant-subjects query on a real mesh with REAL access control (<c>ConfigureMeshBase</c>):
/// users are listed only to a viewer who may grant at the scope (policy
/// <c>user-listing-needs-grant-right</c>, #7908). The in-memory provider's own C# read rule lists every
/// user to every signed-in viewer on the RAW directory read — the negative control below — so a
/// non-granter seeing nobody through the picker's query is the gate, not an empty directory.
/// </summary>
public class GrantSubjectsQueryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "gs-space";
    private const string Other = "gs-other";
    private const string Granter = "gs-granter";
    private const string Viewer = "gs-viewer";
    private const string Alice = "gs-alice";
    private const string Bob = "gs-bob";

    private static MeshNode Person(string id) => new(id)
    {
        NodeType = "User",
        Name = id,
        State = MeshNodeState.Active,
    };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(Space) { NodeType = "Space", Name = Space, State = MeshNodeState.Active },
            new MeshNode(Other) { NodeType = "Space", Name = Other, State = MeshNodeState.Active },
            Person(Granter), Person(Viewer), Person(Alice), Person(Bob),
            // The granter administers the space (the manage-access right the Access Control page demands).
            AssignmentNodeFactory.UserRole(Granter, "Admin", Space),
            // The viewer only reads it — same scope, no right to grant.
            AssignmentNodeFactory.UserRole(Viewer, "Viewer", Space));

    private IObservable<IReadOnlyList<MeshNode>> Listing(string viewer, string query)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var context = new AccessContext { ObjectId = viewer, Name = viewer };
        return LauncherArrangementSource.Fold(access.RunAs(context, () => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))))
            .Select(rows => (IReadOnlyList<MeshNode>)rows.Values.ToList());
    }

    private static bool ListsThePeople(IReadOnlyList<MeshNode> rows)
        => rows.Any(r => r.Path == Alice) && rows.Any(r => r.Path == Bob);

    [Fact(Timeout = 60000)]
    public async Task AGranter_SeesUsers_ANonGranterAtTheSameScope_SeesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = AccessSubjectQueries.UsersAt(Space);

        // A granter sees the users — and this is what proves the grants have loaded, so the
        // non-granter's empty answer below cannot be an answer taken before they did.
        var granted = await Listing(Granter, query).Where(ListsThePeople)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        granted.Should().OnlyContain(r => r.NodeType == "User");

        // The non-granter, same scope, same query: nobody, on the first answer.
        var refused = await Listing(Viewer, query).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        refused.Should().BeEmpty("only someone who may grant at the scope lists users");

        // NEGATIVE CONTROL: the same viewer's RAW directory read does list the people here (the
        // in-memory provider applies the User type's C# read rule), so the empty answer above is the
        // gate refusing — not a directory the viewer could not have seen anyway.
        await Listing(Viewer, AccessSubjectQueries.Users).Where(ListsThePeople)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    [Fact(Timeout = 60000)]
    public async Task TheGranter_SeesNone_WhereTheyHoldNoRight_NorAtTheRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        // Positive control first: at the scope they administer, the granter lists the people.
        await Listing(Granter, AccessSubjectQueries.UsersAt(Space)).Where(ListsThePeople)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        (await Listing(Granter, AccessSubjectQueries.UsersAt(Other)).FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("the right is checked at THE scope the picker grants on");
        (await Listing(Granter, AccessSubjectQueries.UsersAt(null)).FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty("no grant is ever made at the root (AccessAssignmentGuard.CanGrantAt)");
    }

    /// <summary>
    /// The shared query cache (<c>GetQuery</c>) loads its queries AS SYSTEM and filters rows per
    /// subscriber; a root User profile is readable by every signed-in viewer, so the provider must
    /// refuse System outright — a non-granter subscribing there sees nobody. The granter's direct
    /// query beside it is the positive control.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheSharedQueryCache_ListsNobody_ToANonGranter()
    {
        var ct = TestContext.Current.CancellationToken;
        await Listing(Granter, AccessSubjectQueries.UsersAt(Space)).Where(ListsThePeople)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var viewer = new AccessContext { ObjectId = Viewer, Name = Viewer };
        foreach (var query in new[] { AccessSubjectQueries.UsersAt(Space), AccessSubjectQueries.UsersAt(null) })
        {
            var rows = await access.RunAs(viewer, () => Mesh.GetWorkspace().GetQuery($"gs-cache:{query}", query))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            rows.Should().BeEmpty($"'{query}' through the System-loaded cache must not list users to a non-granter");
        }
    }

    /// <summary>A revoked right withdraws the rows on the SAME live subscription.</summary>
    [Fact(Timeout = 60000)]
    public async Task ARevokedRight_WithdrawsTheUsers_OnTheSameSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        const string revoked = "gs-revoked";
        var seen = new System.Reactive.Subjects.ReplaySubject<IReadOnlyList<MeshNode>>();
        using var subscription = Listing(revoked, AccessSubjectQueries.UsersAt(Space)).Subscribe(seen);

        var grant = AssignmentNodeFactory.UserRole(revoked, "Admin", Space);
        await access.RunAsSystem(() => mesh.CreateNode(grant)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await seen.Where(ListsThePeople).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        await access.RunAsSystem(() => mesh.DeleteNode(grant.Path)).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        await seen.SkipWhile(rows => !ListsThePeople(rows)).Where(rows => rows.Count == 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    [Fact(Timeout = 60000)]
    public async Task TheAttributeTemplate_ResolvesTheScopeFromTheAssignmentsNamespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var query = MeshWeaver.Domain.MeshNodeAttribute.ResolveQueries(
            [AccessSubjectQueries.UsersTemplate], $"{Space}/_Access")[0];
        await Listing(Granter, query).Where(ListsThePeople)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await Listing(Viewer, query).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeEmpty();
    }
}
