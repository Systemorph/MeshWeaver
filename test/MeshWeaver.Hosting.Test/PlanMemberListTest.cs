using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A plan's declared MEMBERS (<see cref="PlanTierRanks.Members"/>, <c>content.members</c> on its tier node): a plan
/// that ranks low but includes a named set of higher-tier packages. The maintainer, 2026-11-08, on the self-hosting
/// plan: "Only the listed packages" — Hosting and the harnesses a self-hoster needs, never the rest of Pro, which
/// stays a purchase. One declared rule, read by the registry from the tier node:
/// <list type="bullet">
/// <item>a MEMBER is covered by its plan whatever its tier;</item>
/// <item>a NON-member of a higher tier is NOT covered (Pro stays a purchase);</item>
/// <item>the ranked plans are UNCHANGED: members widen no other plan, and a caller that knows no package id
/// decides by rank alone;</item>
/// <item>the ladder reads the members off the tier node, in whatever shape the content arrives.</item>
/// </list>
/// </summary>
public class PlanMemberListTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly PlanTierRanks Ladder = PlanTierRanks.From(new[]
        {
            ("free", 0, false), ("selfhosting", 5, false), ("personal", 10, false), ("pro", 20, false),
            ("dedicated", 25, true), ("enterprise", 30, false),
        })
        .WithMembers(new[] { ("selfhosting", (System.Collections.Generic.IEnumerable<string>)new[] { "Hosting", "ClaudeCode", " Acp ", "" }) });

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact]
    public void AMember_IsCoveredByItsPlan_WhateverItsTier()
    {
        Assert.True(Ladder.CoversInstance("selfhosting", "enterprise", "Hosting"), "an enterprise-tier member");
        Assert.True(Ladder.CoversInstance("SelfHosting", "pro", "claudecode"), "plan and package ids match case-insensitively");
        Assert.True(Ladder.CoversInstance("selfhosting", "pro", "Acp"), "a member id is trimmed");
        Assert.True(Ladder.Covers("selfhosting", "enterprise", "Hosting"), "a plan-scoped grant entry decides the same");
        Assert.True(Ladder.CoversInstance("selfhosting", "free", "Mail"), "the plan's rank still covers the free baseline");
        Assert.True(Ladder.CoversInstance("selfhosting", null, "Store"), "and untiered platform packages");
    }

    [Fact]
    public void ANonMemberOfAHigherTier_IsNotCovered()
    {
        Assert.False(Ladder.CoversInstance("selfhosting", "pro", "Codex"), "a pro package not on the list stays a purchase");
        Assert.False(Ladder.CoversInstance("selfhosting", "personal", "Chess"), "the plan ranks below personal");
        Assert.False(Ladder.CoversInstance("selfhosting", "enterprise", "Governance"), "nor the enterprise vertical");
        Assert.False(Ladder.CoversInstance("selfhosting", "pro", ""), "a blank id is no member");
        Assert.False(Ladder.CoversInstance("selfhosting", "pro", null), "a missing id is no member");
    }

    [Fact]
    public void TheRankedPlans_AreUnchanged()
    {
        // members widen no other plan
        Assert.False(Ladder.CoversInstance("free", "enterprise", "Hosting"));
        Assert.False(Ladder.CoversInstance("personal", "pro", "ClaudeCode"));
        Assert.False(Ladder.CoversInstance("pro", "enterprise", "Hosting"), "Pro still does not cover an enterprise package");
        Assert.True(Ladder.CoversInstance("pro", "pro", "Codex"));
        Assert.True(Ladder.CoversInstance("enterprise", "enterprise", "Hosting"));
        Assert.True(Ladder.CoversInstance("dedicated", "enterprise", "Governance"), "all-access stays all-access");
        // a caller with no package id decides by rank alone
        Assert.False(Ladder.CoversInstance("selfhosting", "enterprise"));
        Assert.False(Ladder.Covers("selfhosting", "pro"));
        // an unknown plan named in a grant entry licenses nothing, members or not
        Assert.False(Ladder.Covers("gold", "pro", "Hosting"));
        // a ladder with no members is the old ladder
        var plain = PlanTierRanks.From(new[] { ("free", 0, false), ("pro", 20, false) });
        Assert.Empty(plain.Members);
        Assert.False(plain.CoversInstance("free", "pro", "Hosting"));
    }

    [Fact]
    public void TheGrant_DecidesAMemberByItsId()
    {
        var grant = new PluginGrant
        {
            InstanceId = "studio-1",
            Entries = ImmutableList.Create(new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages }),
        };
        var now = DateTimeOffset.UtcNow;
        Assert.True(grant.Allows("Plugins", "Hosting", "enterprise", Ladder, "selfhosting", now));
        Assert.Null(grant.TierRefusal("Plugins", "Hosting", "enterprise", Ladder, "selfhosting", now));
        Assert.False(grant.Allows("Plugins", "Codex", "pro", Ladder, "selfhosting", now));
        Assert.Equal("selfhosting", grant.TierRefusal("Plugins", "Codex", "pro", Ladder, "selfhosting", now));
    }

    [Fact]
    public async Task TheLadder_ReadsTheMembersOffTheTierNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath($"{PlanTierLadder.Namespace}/selfhosting") with
            {
                NodeType = "Markdown",
                Content = JsonSerializer.SerializeToElement(new { rank = 5, allAccess = false, members = new[] { "Hosting", "ClaudeCode" } }),
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the tier is seeded", cancellationToken: ct);
        await access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath($"{PlanTierLadder.Namespace}/pro") with
            {
                NodeType = "Markdown",
                Content = JsonSerializer.SerializeToElement(new { rank = 20, allAccess = false }),
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("pro is seeded", cancellationToken: ct);

        var ladder = Mesh.ServiceProvider.GetRequiredService<PlanTierLadder>();
        var read = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ladder.Read())
            .Where(r => r.Ids.Contains("selfhosting") && r.Ids.Contains("pro"))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the ladder carries both tiers", cancellationToken: ct);
        Assert.True(read.IsMember("selfhosting", "hosting"));
        Assert.True(read.CoversInstance("selfhosting", "enterprise", "Hosting"));
        Assert.False(read.CoversInstance("selfhosting", "pro", "Codex"));
        Assert.False(read.IsMember("pro", "Hosting"), "a plan with no members declares none");
    }
}
