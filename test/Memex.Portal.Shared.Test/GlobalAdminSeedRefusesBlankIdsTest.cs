using System;
using System.Collections.Generic;
using System.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver#5217. <c>Auth:GlobalAdmins</c> now reaches a pod through the chart
/// (<c>Auth__GlobalAdmins__0..2</c>), so its values arrive as environment strings — and a
/// declared-but-blank entry, or a hole between indices, is an ordinary thing for an overlay to
/// produce. The seed used to trim each id and grant it: a blank became an <c>Admin</c> assignment on
/// the <c>Admin</c> partition for the EMPTY username. These pin that a blank grants nothing, a
/// duplicate grants once, and a real id still grants exactly the two assignments it always did.
/// </summary>
public class GlobalAdminSeedRefusesBlankIdsTest
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void ABlankEntry_GrantsNothing()
    {
        var nodes = GlobalAdminSeed.Build(Config(
            ("Auth:GlobalAdmins:0", "alice"),
            ("Auth:GlobalAdmins:1", ""),
            ("Auth:GlobalAdmins:2", "   ")));

        Assert.All(nodes, node =>
        {
            var grant = Assert.IsType<AccessAssignment>(node.Content);
            Assert.False(string.IsNullOrWhiteSpace(grant.AccessObject),
                $"{node.Path} grants Admin to a blank identity");
        });
        Assert.Equal(["Admin/_Access/alice_Access", "Provider/_Access/alice_Access"],
            nodes.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void OnlyBlankEntries_SeedNothingAtAll()
    {
        Assert.Empty(GlobalAdminSeed.Build(Config(("Auth:GlobalAdmins:0", ""))));
    }

    [Fact]
    public void ADuplicateId_IsSeededOnce()
    {
        var nodes = GlobalAdminSeed.Build(Config(
            ("Auth:GlobalAdmins:0", "alice"),
            ("Auth:GlobalAdmins:1", " Alice ")));

        Assert.Equal(2, nodes.Length);
        Assert.Equal(nodes.Length, nodes.Select(n => n.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Control: the shape a real id gets is unchanged — Admin on Admin, Admin on Provider.</summary>
    [Fact]
    public void ARealId_StillGetsBothGrants_ScopedToTheirPartitions()
    {
        var nodes = GlobalAdminSeed.Build(Config(("Auth:GlobalAdmins:0", " bob ")));

        Assert.Collection(nodes.OrderBy(n => n.Path, StringComparer.Ordinal),
            admin =>
            {
                Assert.Equal("Admin/_Access/bob_Access", admin.Path);
                Assert.Equal("Admin", admin.MainNode);
                Assert.Equal("bob", Assert.IsType<AccessAssignment>(admin.Content).AccessObject);
            },
            provider =>
            {
                Assert.Equal("Provider/_Access/bob_Access", provider.Path);
                Assert.Equal("Provider", provider.MainNode);
            });
    }
}
