using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Issue #3890 — the <c>@</c>-reference drill-down answered without the caller's identity and
/// without row-level security, so it named nodes, their TITLES and their node types out of
/// partitions the caller cannot read.</b>
///
/// <para><b>What it was.</b> <c>StorageAdapterMeshQueryProvider.Autocomplete</c> built its request
/// with a hard-coded <c>UserId = null</c> and ran it through
/// <c>RunQueryNodes(…, useSecurityFilter: false)</c>. Same storage, same query shape and the same
/// <c>ValidateRead</c> chain as the secured read — only those two inputs differed. Measured on
/// <c>memex.systemorph.com</c> on 2026-09-10: <c>autocomplete '@/Helvetia/'</c> named five nodes
/// (<c>Helvetia</c>, <c>Helvetia/Engagement</c>, …) for an identity whose <c>get</c> and
/// <c>search</c> on those very paths answered nothing, and the drill-down of a readable partition
/// returned strings like <i>"Pricing Comparison (Internal)"</i> — document titles, not just ids.</para>
///
/// <para><b>Why the pedestrian provider is the whole story even on Postgres.</b>
/// <c>MeshQuery.SelectMatchingProviders</c> hands EVERY registered <c>IMeshQueryProvider</c> every
/// autocomplete and unions the snapshots by path, and
/// <c>PersistenceExtensions.AddPartitionedCoreAndWrapperServices</c> registers this provider
/// <i>alongside</i> a native backend rather than instead of it ("Native query backends (Postgres,
/// Cosmos) register their OWN IMeshQueryProvider alongside this one"). The native provider's
/// autocomplete does carry a user id into its SQL — and its filtered rows were then merged with
/// this provider's unfiltered ones.</para>
///
/// <para><b>The control, in both directions.</b> <see cref="ADeniedNodeIsNotNamedByTheDrillDown"/>
/// is the failing-before assertion; <see cref="AReadableNodeIsStillNamedByTheDrillDown"/> is the
/// control that the filter did not simply empty autocomplete, and it doubles as the proof that the
/// snapshot this suite reads is the CONVERGED one (an instrument that always returned the empty
/// seed would make the denial assertion pass having measured nothing).
/// <see cref="TheOutsiderReallyIsDenied"/> pins the premise both of them rest on, against the very
/// predicate <c>RlsNodeValidator</c> consults.</para>
/// </summary>
public class AutocompleteHonoursReadAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SecretId = "PricingComparison";
    private const string SecretPath = $"{TestPartition}/{SecretId}";

    /// <summary>
    /// A node NAME that is business content rather than an identifier — the half of the disclosure
    /// that made #3890 more than an existence leak.
    /// </summary>
    private const string SecretName = "Pricing Comparison (Internal)";

    /// <summary>
    /// A signed-in caller with no grant anywhere. Not an attacker shape: the MCP <c>autocomplete</c>
    /// tool is gated on <c>RequireAuthenticatedUser()</c> and nothing else, so ANY authenticated
    /// user of the portal reaches the drill-down exactly like this.
    /// </summary>
    private static readonly AccessContext Outsider = new()
    {
        ObjectId = "outsider",
        Name = "Outsider",
        Email = "outsider@example.com",
        Roles = [],
    };

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh — the latter chains PublicAdminAccess(), which
    // grants Public→Admin on every default partition and would make every identity an
    // administrator, so "the outsider cannot read this" would be vacuously… false. RLS has to be
    // genuinely enforced for any of this to mean anything.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private Task<MeshNode> CreateSecret() =>
        MeshService.CreateNode(
                new MeshNode(SecretId, TestPartition) { Name = SecretName, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the admin owns this partition");

    /// <summary>
    /// The drill-down the issue measured: <c>@/{TestPartition}/</c>, i.e. an empty prefix under a
    /// concrete partition. <c>LastAsync</c> rather than <c>FirstAsync</c> because
    /// <c>MeshQuery.Autocomplete</c> seeds every provider with <c>.StartWith(empty)</c> so the
    /// CombineLatest can emit progressively — the first emission is the all-empty frame, and the
    /// stream completes once every provider has produced, so the LAST one is the converged answer.
    /// <see cref="AReadableNodeIsStillNamedByTheDrillDown"/> is what proves this reads the
    /// converged frame and not the seed.
    /// </summary>
    private Task<IReadOnlyCollection<QueryResult>> DrillDown(string because) =>
        MeshService.Autocomplete(TestPartition, "", AutocompleteMode.PathFirst, limit: 50)
            .LastAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(because);

    /// <summary>
    /// Switches the ambient viewer to <see cref="Outsider"/> and CHECKS THE SWITCH TOOK. Without
    /// that check a mis-wired identity hop would leave the assertions running as the admin, where
    /// "the drill-down named the node" is the correct answer and the test would report a pass for
    /// the opposite of the reason it was written.
    /// </summary>
    private void BecomeTheOutsider()
    {
        Access.SetCircuitContext(Outsider);
        (Access.Context?.ObjectId ?? Access.CircuitContext?.ObjectId)
            .Should().Be(Outsider.ObjectId,
                "every assertion below is about what THIS identity sees — if the ambient viewer "
                + "were still the test's admin the leak assertion would pass without measuring it");
    }

    /// <summary>
    /// The premise: <see cref="Outsider"/> genuinely holds no Read on the node, measured against
    /// <c>hub.CheckPermission</c> — the same fold <c>RlsNodeValidator.CheckPermission</c> consults
    /// for <see cref="NodeOperation.Read"/>. Both directions, so a fold that answered <c>false</c>
    /// for everybody (a broken instrument) fails here rather than silently validating the leak test.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheOutsiderReallyIsDenied()
    {
        await CreateSecret();

        (await Mesh.CheckPermission(SecretPath, Outsider.ObjectId, Permission.Read)
                .Should().Within(TestTimeouts.Convergence).Emit("the permission fold answers"))
            .Should().BeFalse(
                $"'{Outsider.ObjectId}' holds no grant at any scope over '{SecretPath}'");

        (await Mesh.CheckPermission(SecretPath, TestUsers.Admin.ObjectId, Permission.Read)
                .Should().Within(TestTimeouts.Convergence).Emit("the permission fold answers"))
            .Should().BeTrue(
                "the root Admin grant the test base seeds must still read — a fold that denied "
                + "everyone would make the denial above meaningless");
    }

    /// <summary>
    /// 🚨 THE REGRESSION. Before the fix this fails on the first assertion with the leaked path in
    /// the message; the second names the leaked TITLE, which is the part that makes this a
    /// disclosure rather than an enumeration.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ADeniedNodeIsNotNamedByTheDrillDown()
    {
        await CreateSecret();

        BecomeTheOutsider();

        var suggestions = await DrillDown(
            "the drill-down answers for any authenticated caller");

        suggestions.Select(s => s.Path).Should().NotContain(SecretPath,
            "autocomplete reads the same storage, through the same query shape, as the `get` that "
            + "refuses this caller — a suggestion the caller cannot then open is a disclosure of "
            + "the node's existence and its place in someone else's tree (#3890)");

        suggestions.Select(s => s.Name).Should().NotContain(SecretName,
            "a node's Name is business content — the drill-down was returning the document titles "
            + "of workspaces the caller has no grant on (#3890)");
    }

    /// <summary>
    /// The control on the other side: filtering must not simply empty the feature. Also the proof
    /// that <see cref="DrillDown"/> reads the converged snapshot rather than the empty seed — if it
    /// did not, this fails.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AReadableNodeIsStillNamedByTheDrillDown()
    {
        await CreateSecret();

        var suggestions = await DrillDown("the admin drill-down converges on the created node");

        suggestions.Select(s => s.Path).Should().Contain(SecretPath,
            "the test base seeds a root Admin grant for this identity, so the node is readable and "
            + "must still be suggested");

        suggestions.Select(s => s.Name).Should().Contain(SecretName,
            "and the suggestion still carries the node's display name for the people entitled to it");
    }
}
