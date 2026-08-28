using System;
using System.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// <c>OnboardingMiddleware.LoadUserRoles</c> is the read that DECIDES a viewer's roles, so it is a
/// security-fold read wearing ordinary clothes. It was written as neither: a hand-rolled,
/// RLS-wrapped, <c>limit:10</c> query that never reached <see cref="SecurityQueries"/>.
///
/// <para>Both defects fail the same silent way, which is why neither showed up as an error. A
/// truncated grant list and an RLS-stripped grant list are both just a SHORTER list, and the caller
/// stamps it as the viewer's roles — an availability/authorization failure that surfaces only as
/// screens answering "Access denied", with nothing logged and nothing thrown. The caller's own
/// remarks describe that outcome for the timeout case; these are two more ways to reach it.</para>
/// </summary>
public class LoadUserRolesIsASystemReadTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The number of grants deliberately exceeds the <c>limit:10</c> this query carried. It is not
    /// an arbitrary "lots": ten passes and eleven does not, so the bound is what the test pins.
    /// </summary>
    private const int GrantCount = 14;

    /// <summary>
    /// Deliberately BELOW the old `limit:10`, so the identity test cannot pass or fail for the
    /// truncation reason — it isolates the ambient-identity variable on its own.
    /// </summary>
    private const int UntruncatedGrantCount = 3;

    private async Task<string> SeedGrantsAsync(string partition, string username, int count)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        for (var i = 0; i < count; i++)
            await meshService.CreateNode(new MeshNode($"grant{i}", $"{partition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"grant {i}",
                MainNode = partition,
                State = MeshNodeState.Active,
                Content = new AccessAssignment
                {
                    AccessObject = username,
                    DisplayName = username,
                    Roles = [new RoleAssignment { Role = $"TestRole{i}", Denied = false }]
                }
            }).Should().Within(30.Seconds()).Emit();
        return partition;
    }

    /// <summary>
    /// 🚨 A viewer holding more grants than the query's page size lost the overflow SILENTLY.
    /// `limit:10` is honoured when it arrives in the query string, so grant eleven onward simply
    /// were not in the answer — and the fold unions what it got, so the result is a plausible,
    /// shorter, wrong role set. This is the truncation #2011 fixed for the security fold, which
    /// <see cref="SecurityQueries.Enumeration"/> exists to make structurally impossible; a query
    /// that never reaches that seam is the one way back to it.
    /// </summary>
    [Fact]
    public async Task Every_grant_is_read_not_the_first_page_of_them()
    {
        var username = $"many{Guid.NewGuid():N}"[..16];
        var partition = await SeedGrantsAsync($"p{Guid.NewGuid():N}"[..12], username, GrantCount);

        var outcome = await OnboardingMiddleware
            .LoadUserRoles(Mesh.GetWorkspace(), username, logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeFalse("the read converged");
        outcome.Value.Should().HaveCount(
            GrantCount,
            "every AccessAssignment naming this user must be folded, not the first page of them — "
            + $"a truncated read is indistinguishable from holding fewer roles (partition {partition})");
    }

    /// <summary>
    /// 🚨 The read must not be gated on the identity it is building.
    ///
    /// <para><c>GetQuery</c> is RLS-wrapped per subscriber, and AccessAssignment nodes live in
    /// <c>{scope}/_Access</c> — readable only through the very roles this call exists to resolve.
    /// Evaluating it under the caller's ambient identity therefore answers with the subset an
    /// unbuilt identity can see, which can only ever be SMALLER than the truth. Running it as
    /// System grants the viewer nothing they do not already hold; it just stops the answer
    /// depending on who happened to be on the thread.</para>
    ///
    /// <para>🚨 <b>This test is a RATCHET, not a red-proof, and must not be read as one.</b> It
    /// passes on the unfixed code too: the per-user filter is inert unless RLS is installed on the
    /// mesh (<c>WrapWithPerUserRls</c> returns the upstream unchanged when the hub has no
    /// <c>EffectivePermissionsDelegate</c>), which a monolith test mesh does not configure. So it
    /// cannot demonstrate the recursion the System read removes — it pins the INVARIANT that the
    /// answer is identity-independent, and fails if a future change makes this read consult the
    /// asker. The truncation sibling above is the case that actually goes red on the old code.</para>
    /// </summary>
    [Fact]
    public async Task The_answer_does_not_depend_on_who_is_asking()
    {
        var username = $"subj{Guid.NewGuid():N}"[..16];
        await SeedGrantsAsync($"q{Guid.NewGuid():N}"[..12], username, UntruncatedGrantCount);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var previous = access.CircuitContext;
        // A signed-in stranger with no grants anywhere. BOTH ambient slots are switched: the fold
        // reads `Context ?? CircuitContext`, so setting only the circuit one leaves the test base's
        // admin identity in front of it and the test measures nothing.
        var stranger = new AccessContext
        {
            ObjectId = $"stranger{Guid.NewGuid():N}"[..16],
            Name = "Unprivileged Stranger"
        };
        try
        {
            access.SetCircuitContext(stranger);
            using var ambient = access.SwitchAccessContext(stranger);

            var outcome = await OnboardingMiddleware
                .LoadUserRoles(Mesh.GetWorkspace(), username, logger: null)
                .Should().Emit();

            outcome.IsUnavailable.Should().BeFalse("the read converged");
            outcome.Value.Should().HaveCount(
                UntruncatedGrantCount,
                "role resolution is identity establishment and runs as System — reading it under "
                + "the ambient identity gates the answer on the identity being built, and returns "
                + "the strictly smaller set that identity can see");
        }
        finally
        {
            access.SetCircuitContext(previous);
        }
    }
}
