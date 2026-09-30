using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the BROAD-GRANT GUARD's shapes (<see cref="BroadGrantGuard"/>): a grant to everybody, a
/// grant the platform writes for somebody else, and a partition access policy are written only by
/// an executing governed activity. The incident: 2026-09-29, a new free plugin granted to 72 users
/// in three minutes by a System sweep writing "for no one in particular".
/// </summary>
public class BroadGrantGuardTest
{
    private static readonly AccessContext System = new() { ObjectId = WellKnownUsers.System };
    private static readonly AccessContext Person = new() { ObjectId = "rbuergi" };

    private static (MeshNode Node, AccessAssignment Assignment) Grant(
        string subject, bool denied = false, string scope = "Parties", string role = "Viewer")
    {
        var assignment = new AccessAssignment
        {
            AccessObject = subject,
            Roles = [new RoleAssignment { Role = role, Denied = denied }],
        };
        var node = new MeshNode($"{subject}_Access", $"{scope}/_Access")
        {
            NodeType = AccessAssignmentGuard.AccessAssignmentNodeType,
            MainNode = scope,
            Content = assignment,
        };
        return (node, assignment);
    }

    // ── the three shapes ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Public")]
    [InlineData("Anonymous")]
    public void AGrantToEverybody_IsBroad_WhoeverWritesIt(string subject)
    {
        var (node, assignment) = Grant(subject);
        (BroadGrantGuard.Evaluate(node, assignment, Person)?.Kind).Should().Be(BroadGrantKind.PublicSubject);
        (BroadGrantGuard.Evaluate(node, assignment, System)?.Kind).Should().Be(BroadGrantKind.PublicSubject);
    }

    /// <summary>A Denied assignment only removes access — the Store's gating — and always passes.</summary>
    [Fact]
    public void ADenyForEverybody_IsNotBroad()
    {
        var (node, assignment) = Grant("Public", denied: true);
        BroadGrantGuard.Evaluate(node, assignment, System).Should().BeNull();
    }

    /// <summary>The 72-users shape: System writing a grant for a user it is not acting for.</summary>
    [Fact]
    public void ASystemGrantForNoOneInParticular_IsBroad()
    {
        var (node, assignment) = Grant("jdoe");
        var finding = BroadGrantGuard.Evaluate(node, assignment, System);
        finding.Should().Be(new BroadGrantFinding(
            BroadGrantKind.SystemForOther, node.Path, "jdoe", WellKnownUsers.System));
    }

    /// <summary>A user's own acquisition (subscription, coupon, purchase) stamps OnBehalfOf and passes.</summary>
    [Fact]
    public void ASystemGrantOnBehalfOfItsOwnSubject_Passes()
    {
        var (node, assignment) = Grant("jdoe");
        BroadGrantGuard.Evaluate(node, assignment, System with { OnBehalfOf = "jdoe" }).Should().BeNull();
    }

    [Fact]
    public void ASystemGrantOnBehalfOfSomebodyElse_IsBroad()
    {
        var (node, assignment) = Grant("jdoe");
        (BroadGrantGuard.Evaluate(node, assignment, System with { OnBehalfOf = "someone" })?.Kind)
            .Should().Be(BroadGrantKind.SystemForOther);
    }

    /// <summary>A person sharing their space with a colleague is an ordinary write, not a broad one.</summary>
    [Fact]
    public void APersonGrantingOneColleague_IsNotBroad()
    {
        var (node, assignment) = Grant("colleague", role: "Editor", scope: "rbuergi/Project");
        BroadGrantGuard.Evaluate(node, assignment, Person).Should().BeNull();
    }

    [Fact]
    public void ThePlatformsOwnGrant_IsNotBroad()
    {
        var (node, assignment) = Grant(WellKnownUsers.System, role: "Admin");
        BroadGrantGuard.Evaluate(node, assignment, System).Should().BeNull();
    }

    [Fact]
    public void APartitionAccessPolicy_IsBroad_WhoeverWritesIt()
    {
        var policy = new MeshNode("_Policy", "Parties") { NodeType = BroadGrantGuard.AccessPolicyNodeType };
        (BroadGrantGuard.Evaluate(policy, null, Person)?.Kind).Should().Be(BroadGrantKind.AccessPolicy);
    }

    [Fact]
    public void AnOrdinaryNode_IsNotEvaluated()
    {
        var doc = new MeshNode("Doc", "rbuergi") { NodeType = "Markdown" };
        BroadGrantGuard.Evaluate(doc, null, System).Should().BeNull();
    }

    // ── the one way through: a governed activity ───────────────────────────────────────────

    /// <summary>Both the context and the node must name the same activity; either alone is no claim.</summary>
    [Fact]
    public void AClaimNeedsTheContextAndTheNodeToAgree()
    {
        const string activity = "Governance/Activities/grant-public-guide";
        var node = new MeshNode("Public_Access", "Parties/_Access")
        {
            NodeType = AccessAssignmentGuard.AccessAssignmentNodeType,
            MainNode = "Parties",
            Content = new JsonObject { ["accessObject"] = "Public", ["governedBy"] = activity },
        };

        BroadGrantGuard.ClaimedActivity(node, System with { GovernedBy = activity }, null).Should().Be(activity);
        BroadGrantGuard.ClaimedActivity(node, System, null).Should().BeNull();
        BroadGrantGuard.ClaimedActivity(node, System with { GovernedBy = "Governance/Activities/other" }, null).Should().BeNull();

        var unmarked = node with { Content = new JsonObject { ["accessObject"] = "Public" } };
        BroadGrantGuard.ClaimedActivity(unmarked, System with { GovernedBy = activity }, null).Should().BeNull();
    }

    /// <summary>The claim survives a typed hop: the executor's back-reference is a real field on the grant.</summary>
    [Fact]
    public void ATypedGrantCarriesItsClaim()
    {
        const string activity = "Governance/Activities/grant-public-guide";
        var (node, assignment) = Grant("Public");
        var typed = node with { Content = assignment with { GovernedBy = activity } };
        BroadGrantGuard.ClaimedActivity(typed, System with { GovernedBy = activity }, null).Should().Be(activity);
    }

    public static IEnumerable<object[]> ActivityStates() =>
    [
        ["Executing", "Governance/Standards/access.grant-broad", true],
        [4, "access.grant-broad", true],
        ["Ready", "access.grant-broad", false],
        ["Done", "access.grant-broad", false],
        ["Executing", "release.cut", false],
    ];

    [Theory]
    [MemberData(nameof(ActivityStates))]
    public void OnlyAnExecutingAllowlistedActivity_Counts(object state, string standard, bool expected)
    {
        var content = new JsonObject
        {
            ["state"] = state is int n ? JsonValue.Create(n) : JsonValue.Create((string)state),
            ["standard"] = standard,
        };
        var activity = new MeshNode("grant", "Governance/Activities") { Content = content };
        MeshExtensions.GovernedActivityExecuting(activity, BroadGrantGuard.DefaultGovernedStandards, null)
            .Should().Be(expected);
    }

    // ── settings ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheModeDefaultsToLogOnly()
    {
        BroadGrantGuard.Mode(null).Should().Be(BroadGrantMode.LogOnly);
        BroadGrantGuard.Mode(Config((BroadGrantGuard.ModeKey, "nonsense"))).Should().Be(BroadGrantMode.LogOnly);
        BroadGrantGuard.Mode(Config((BroadGrantGuard.ModeKey, "Enforce"))).Should().Be(BroadGrantMode.Enforce);
    }

    [Fact]
    public void TheAllowlistCanBeReplacedByConfiguration()
    {
        BroadGrantGuard.GovernedStandards(null).Should().Contain("access.grant-broad");
        var configured = BroadGrantGuard.GovernedStandards(Config((BroadGrantGuard.StandardsKey + ":0", "only.this")));
        configured.Count.Should().Be(1);
        configured.Should().Contain("only.this");
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}
