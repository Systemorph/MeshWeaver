#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
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
/// A caller with no grant must not find it through <c>query-nodes</c> or <c>search</c>; the seeded
/// admin, who may read <c>Admin/_Access</c>, still does — the positive control.</para>
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
            .AddMeshNodes(AssignmentNodeFactory.UserRole(SeededAdmin, "Admin", "Admin"));

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
}
