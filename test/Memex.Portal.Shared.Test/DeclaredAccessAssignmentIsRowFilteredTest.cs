#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An access grant DECLARED in configuration is row-filtered like a stored one.</b>
///
/// <para>The static query provider serves configuration-declared nodes (<c>AddMeshNodes</c>,
/// <see cref="MeshWeaver.Mesh.Services.IStaticNodeProvider"/>) from memory, and it served them to
/// every caller without the per-user row filter the storage providers apply. Almost everything
/// declared there is public by nature — type definitions, built-in roles, partition definitions,
/// documentation — but one shipped declaration is not: the platform-admin seed
/// (<c>Auth:GlobalAdmins</c> → <c>GlobalAdminSeed</c>) declares an <c>AccessAssignment</c> per
/// listed user under <c>Admin/_Access</c>, so any authenticated caller could list who holds
/// platform admin.</para>
///
/// <para>The seed is reproduced here with the same shape (an Admin grant at scope <c>Admin</c>).
/// A caller with no grant must not find it through <c>query-nodes</c>, <c>search</c> or an
/// autocomplete drill-down under <c>Admin/_Access</c>; the seeded admin, who may read
/// <c>Admin/_Access</c>, still does — the positive control.</para>
/// </summary>
public class DeclaredAccessAssignmentIsRowFilteredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SeededAdmin = "declaredgrant-admin";
    private const string Stranger = "declaredgrant-stranger";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every caller could read Admin/_Access and the filter would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole(SeededAdmin, "Admin", "Admin"),
                new MeshNode(SatelliteNote, "Admin/_Access") { NodeType = "Markdown", Name = SatelliteNote });

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "declaredgrant",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DeclaredAccessAssignmentIsRowFilteredTest)));

    private Task<string> As(string userId, Func<MeshOperations, IObservable<string>> read)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        return read(new MeshOperations(SessionHub())).Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private const string Grants = "namespace:Admin/_Access nodeType:AccessAssignment";

    [Fact]
    public async Task QueryNodes_AStranger_DoesNotListTheDeclaredGrant()
    {
        var answer = await As(Stranger, ops => ops.QueryNodes(Grants));
        Output.WriteLine($"stranger: {answer}");

        answer.Should().NotContain(SeededAdmin,
            "the stranger may not read Admin/_Access — a declared grant must not name who holds platform admin");
    }

    [Fact]
    public async Task Search_AStranger_DoesNotListTheDeclaredGrant()
    {
        var answer = await As(Stranger, ops => ops.Search(Grants));
        Output.WriteLine($"stranger: {answer}");

        answer.Should().NotContain(SeededAdmin,
            "the stranger may not read Admin/_Access — a declared grant must not name who holds platform admin");
    }

    [Fact]
    public async Task QueryNodes_TheSeededAdmin_StillListsIt()
    {
        var answer = await As(SeededAdmin, ops => ops.QueryNodes(Grants));
        Output.WriteLine($"admin: {answer}");

        answer.Should().Contain(SeededAdmin,
            "the seeded admin may read Admin/_Access — if this fails, the filter above proves nothing");
    }
    /// <summary>
    /// A declared node on the same satellite path whose type is NOT excluded from autocomplete.
    /// <c>AccessAssignment</c> itself is (<c>AutocompleteExcludedNodeTypes</c>), so a drill-down
    /// never suggested the seed above — a probe of it would pass with or without the row filter.
    /// This row is what makes the filter observable on the autocomplete surface.
    /// </summary>
    private const string SatelliteNote = "declaredgrant-note";

    /// <summary>
    /// The paths the declared-node provider suggests to <paramref name="userId"/> for an autocomplete
    /// drill-down under <c>Admin/_Access</c> — the provider itself, the leaf every
    /// <c>MeshQuery.Autocomplete</c> fan-out (and so the chat drill-down) reaches. One snapshot, then
    /// complete: the catalog is in memory.
    /// </summary>
    private Task<string> AutocompleteAs(string userId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        return Mesh.ServiceProvider.GetRequiredService<StaticNodeQueryProvider>()
            .Autocomplete("Admin/_Access", "", Mesh.JsonSerializerOptions, limit: 50)
            .Select(rows => string.Join("\n", rows.Select(r => r.Path)))
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Autocomplete_AStranger_IsNotSuggestedADeclaredSatelliteRow()
    {
        var answer = await AutocompleteAs(Stranger);
        Output.WriteLine($"stranger: {answer}");

        answer.Should().NotContain(SatelliteNote,
            "an autocomplete drill-down under Admin/_Access is the same read as search — a declared row there is filtered for the stranger exactly as a query of it is");
    }

    [Fact]
    public async Task Autocomplete_TheSeededAdmin_IsStillSuggestedIt()
    {
        var answer = await AutocompleteAs(SeededAdmin);
        Output.WriteLine($"admin: {answer}");

        answer.Should().Contain(SatelliteNote,
            "the seeded admin may read Admin/_Access — if this fails, the stranger's missing suggestion proves nothing");
    }
}
