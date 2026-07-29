using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ACCESS-GRANT SCOPE INVARIANT: a grant must be scoped to the node it is filed under.
///
/// <para><b>What went wrong.</b> A grant is scoped by <c>MainNode</c>, NOT by its folder. So
/// <c>Admin/_Access/{user}_Access</c> with an EMPTY <c>MainNode</c> is not "admin of the Admin
/// partition" — it is a <b>ROOT</b> grant: All on every partition, every space and every user's
/// private home, by scope inheritance. It reads as harmless in the node tree.</para>
///
/// <para>memex, 2026-07-28: <b>43 accounts</b> held that shape (empty <c>node_path_prefix</c> in
/// <c>admin.user_effective_permissions</c>) against exactly ONE correctly-scoped platform admin —
/// including external course participants who had merely redeemed a coupon. They accrued one per
/// user over two weeks and were still being created that day.</para>
///
/// <para>Every KNOWN writer sets <c>MainNode</c> correctly, so an unknown path produces them. That
/// is why this is guarded structurally at the create boundary rather than fixed writer by writer.</para>
/// </summary>
public class AccessAssignmentGuardTest
{
    private static MeshNode Grant(string path, string? mainNode, string nodeType = "AccessAssignment")
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(slash < 0 ? path : path[(slash + 1)..], slash < 0 ? "" : path[..slash])
        {
            NodeType = nodeType,
            // Deliberately null in some cases: a null MainNode is one of the shapes under test
            // (it reaches the evaluator as root scope exactly like the empty string does).
            MainNode = mainNode!
        };
    }

    // ── ScopeFromPath ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin/_Access/rbuergi_Access", "Admin")]
    [InlineData("AgenticEngineering/_Access/sglauser_Access", "AgenticEngineering")]
    [InlineData("Store/Plugin/_Access/x_Access", "Store/Plugin")]   // scopes nest
    [InlineData("rbuergi/_Access/rbuergi_Access", "rbuergi")]       // a user's own home
    public void ScopeFromPath_ReadsTheScopeBeforeTheAccessFolder(string path, string expected) =>
        AccessAssignmentGuard.ScopeFromPath(path).Should().Be(expected);

    /// <summary>A root-level <c>_Access/{id}</c> encodes the ROOT scope — the data-superuser shape.</summary>
    [Fact]
    public void ScopeFromPath_RootLevelAccessIsTheEmptyScope() =>
        AccessAssignmentGuard.ScopeFromPath("_Access/rbuergi_Access").Should().Be("");

    [Theory]
    [InlineData("Admin/Invitation/abc")]
    [InlineData("")]
    [InlineData(null)]
    public void ScopeFromPath_NonGrantPathsAreNotGrantPaths(string? path) =>
        AccessAssignmentGuard.ScopeFromPath(path).Should().BeNull();

    // ── The invariant ────────────────────────────────────────────────────────────────────

    /// <summary>🚨 THE 43-SUPERUSER BUG: empty MainNode under a scope folder means ROOT.</summary>
    [Fact]
    public void EmptyMainNode_IsRejected_BecauseItGrantsRootNotTheFolder()
    {
        var node = Grant("Admin/_Access/rbuergi_Access", mainNode: "");

        AccessAssignmentGuard.IsScopeInvalid(node, out var reason).Should().BeTrue(
            "an empty MainNode grants ROOT — every partition — not the Admin partition");
        reason.Should().Contain("ROOT");
        reason.Should().Contain("MainNode='Admin'", "the message must say exactly how to fix it");
    }

    [Fact]
    public void NullMainNode_IsRejectedToo()
    {
        var node = Grant("Admin/_Access/x_Access", mainNode: null);

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeTrue();
    }

    /// <summary>The correct platform-admin shape — the one row on memex that was right.</summary>
    [Fact]
    public void MainNodeMatchingThePath_IsValid()
    {
        var node = Grant("Admin/_Access/rsalzmann_Access", mainNode: "Admin");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    /// <summary>Every user is admin of their OWN partition — that shape must keep working.</summary>
    [Fact]
    public void UsersOwnHomeGrant_IsValid()
    {
        var node = Grant("albiona.emiri/_Access/albiona.emiri_Access", mainNode: "albiona.emiri");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    /// <summary>Course entitlement grants (PluginGate.Enroll) must keep working.</summary>
    [Fact]
    public void PluginEntitlementGrant_IsValid()
    {
        var node = Grant("AgenticEngineering/_Access/sglauser_Access", mainNode: "AgenticEngineering");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    /// <summary>A grant pointing somewhere OTHER than its folder silently grants elsewhere —
    /// arguably worse than the empty case, because it looks deliberate.</summary>
    [Fact]
    public void MismatchedMainNode_IsRejected()
    {
        var node = Grant("AgenticEngineering/_Access/x_Access", mainNode: "Underwriting");

        AccessAssignmentGuard.IsScopeInvalid(node, out var reason).Should().BeTrue();
        reason.Should().Contain("Underwriting");
        reason.Should().Contain("AgenticEngineering");
    }

    /// <summary>
    /// THE MEMEX SHAPE, and the reason this guard exists: a grant filed in the Admin partition —
    /// where it reads as an ordinary platform-admin grant — whose empty MainNode silently scopes it
    /// to ROOT instead. 34 accounts held exactly this, 21 of them course participants who had only
    /// redeemed a coupon. It is a MISMATCH (path says "Admin", MainNode says root), so the
    /// consistency rule catches it; no separate root-grant rule is needed for the incident.
    /// </summary>
    [Fact]
    public void TheAdminFolderRootGrant_IsRejected()
    {
        var node = Grant("Admin/_Access/rbuergi_Access", mainNode: "");

        AccessAssignmentGuard.IsScopeInvalid(node, out var reason).Should().BeTrue();
        reason.Should().Contain("ROOT");
        reason.Should().Contain("Admin");
    }

    /// <summary>
    /// A SELF-CONSISTENT root grant passes the write boundary — deliberately, and this pins it so
    /// the decision is visible rather than looking like an oversight.
    ///
    /// <para>It is still the superuser shape, but it is not what produced the incident (those were
    /// mismatches, above) and it is how the test harness grants mesh-wide rights —
    /// <c>AssignmentNodeFactory.UserRole(user, role)</c> with no scope, at ~200 call sites, plus
    /// <c>TestUsers.PublicAdminAccess()</c>'s root entry. Refusing it here failed four of six CI
    /// shards, because those tests could then be granted nothing at all.</para>
    ///
    /// <para>What keeps it out of reach in practice is <see cref="AccessAssignmentGuard.CanGrantAt"/>:
    /// the access UI offers no grant surface in a root context, so this shape cannot be produced by
    /// a human clicking. Narrowing it further means rescoping those call sites first.</para>
    /// </summary>
    [Fact]
    public void ASelfConsistentRootGrant_IsAllowedAtTheWriteBoundary_ButNeverOfferedInTheUi()
    {
        var node = Grant("_Access/rbuergi_Access", mainNode: "");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse(
            "the harness grants mesh-wide rights this way; the incident shape was a MISMATCH");
        AccessAssignmentGuard.CanGrantAt("").Should().BeFalse(
            "…and the UI must never offer it — that is what closes the hole a human could open");
    }

    /// <summary>Case must not decide whether someone becomes a superuser.</summary>
    [Fact]
    public void ScopeComparison_IsCaseInsensitive()
    {
        var node = Grant("Admin/_Access/x_Access", mainNode: "admin");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    /// <summary>The guard is about AccessAssignments only — it must not reject unrelated nodes that
    /// happen to live under an _Access folder.</summary>
    [Fact]
    public void NonAccessAssignmentNodes_AreIgnored()
    {
        var node = Grant("Admin/_Access/readme", mainNode: "", nodeType: "Markdown");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    [Fact]
    public void NullNode_IsIgnored() =>
        AccessAssignmentGuard.IsScopeInvalid(null, out _).Should().BeFalse();

    /// <summary>An AccessAssignment that is not on a grant path at all is not this guard's business.</summary>
    [Fact]
    public void AssignmentOffAGrantPath_IsIgnored()
    {
        var node = Grant("Admin/Invitation/abc", mainNode: "");

        AccessAssignmentGuard.IsScopeInvalid(node, out _).Should().BeFalse();
    }

    // ── The navigation context that produces the shape ───────────────────────────────────
    //
    // `nodePath` in the access-control area is host.Hub.Address — the NAVIGATION CONTEXT, not the
    // URL. At root it is EMPTY, AccessNamespace("") becomes a root-level "_Access" folder, and both
    // creation sites set MainNode = nodePath = "". That is a superuser mintable from a button.

    [Theory]
    [InlineData("Admin")]
    [InlineData("AgenticEngineering")]
    [InlineData("Store/Plugin")]
    [InlineData("rbuergi")]
    public void CanGrantAt_APartitionContext_IsAllowed(string scope) =>
        AccessAssignmentGuard.CanGrantAt(scope).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CanGrantAt_TheRootContext_IsRefused(string? scope) =>
        AccessAssignmentGuard.CanGrantAt(scope).Should().BeFalse(
            "at root there is no partition to scope to — a grant there is a platform-wide superuser");

    [Fact]
    public void EnsureScopeValid_ThrowsOnTheRootShape()
    {
        var node = Grant("Admin/_Access/x_Access", mainNode: "");

        var act = () => AccessAssignmentGuard.EnsureScopeValid(node);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureScopeValid_PassesTheCorrectShape()
    {
        var node = Grant("Admin/_Access/x_Access", mainNode: "Admin");

        var act = () => AccessAssignmentGuard.EnsureScopeValid(node);

        act.Should().NotThrow();
    }
}
