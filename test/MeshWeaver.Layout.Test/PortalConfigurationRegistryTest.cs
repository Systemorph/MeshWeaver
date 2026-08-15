#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The registry that lets a runtime-loaded plugin configure the portal hub — so it can ship Blazor
/// views the image was not built with.
///
/// <para>Every rule here fails SILENTLY when broken: a lost contribution is a view that does not
/// render, a duplicated one is an ALC that never unloads, and an unstable order is two replicas
/// rendering differently. None of them throws anywhere near the cause.</para>
/// </summary>
public class PortalConfigurationRegistryTest
{
    /// <summary>A delegate distinguishable by identity; the registry never invokes it.</summary>
    private static Func<MessageHubConfiguration, MessageHubConfiguration> Marker() => c => c;

    [Fact]
    public void AContributionIsHandedBack()
    {
        var registry = new PortalConfigurationRegistry();
        var contribution = Marker();

        registry.Set("mesh/Plugins/ThreeBody", contribution);

        Assert.Same(contribution, Assert.Single(registry.Current).Configure);
    }

    [Fact]
    public void ReRegisteringAnOwnerREPLACESIt()
    {
        // 🚨 THE rule. A NodeType recompile mints a NEW collectible AssemblyLoadContext and
        // re-registers, so the old delegate closes over types from the OLD assembly. Appending
        // would keep invoking it on every portal hub built afterwards — pinning that ALC against
        // unload (an ALC leak already caused an OOM on memex-cloud) and putting two CLR identities
        // of the same view type into one portal.
        var registry = new PortalConfigurationRegistry();
        var build1 = Marker();
        var build2 = Marker();

        registry.Set("mesh/Plugins/ThreeBody", build1);
        registry.Set("mesh/Plugins/ThreeBody", build2);

        Assert.Same(build2, Assert.Single(registry.Current).Configure);
    }

    [Fact]
    public void DifferentOwnersBothContribute()
    {
        var registry = new PortalConfigurationRegistry();

        registry.Set("mesh/Plugins/A", Marker());
        registry.Set("mesh/Plugins/B", Marker());

        Assert.Equal(2, registry.Current.Count);
        Assert.Equal(["mesh/Plugins/A", "mesh/Plugins/B"], registry.Owners);
        // The owner travels with the delegate — that is what per-user filtering will select on.
        Assert.Equal(["mesh/Plugins/A", "mesh/Plugins/B"],
            registry.Current.Select(contribution => contribution.Owner));
    }

    [Fact]
    public void TheOrderDoesNotDependOnRegistrationOrder()
    {
        // Registration order is whichever NodeType hub activated first, which varies per pod and
        // per boot. Two replicas applying the same contributions in different orders would resolve
        // a contested view mapping differently — "it renders differently on one pod", with nothing
        // to explain it.
        var first = new PortalConfigurationRegistry();
        first.Set("mesh/Plugins/Zulu", Marker());
        first.Set("mesh/Plugins/Alpha", Marker());

        var second = new PortalConfigurationRegistry();
        second.Set("mesh/Plugins/Alpha", Marker());
        second.Set("mesh/Plugins/Zulu", Marker());

        Assert.Equal(first.Owners, second.Owners);
        Assert.Equal(["mesh/Plugins/Alpha", "mesh/Plugins/Zulu"], first.Owners);
    }

    [Fact]
    public void RemovingAnOwnerDropsItsContribution()
    {
        var registry = new PortalConfigurationRegistry();
        registry.Set("mesh/Plugins/Gone", Marker());

        Assert.True(registry.Remove("mesh/Plugins/Gone"));
        Assert.Empty(registry.Current);
        Assert.False(registry.Remove("mesh/Plugins/Gone"));
    }

    [Fact]
    public void CurrentIsASnapshot()
    {
        // A portal hub is configured once, at creation. Handing out a live view would let a
        // registration that lands mid-configuration change the list being folded.
        var registry = new PortalConfigurationRegistry();
        registry.Set("mesh/Plugins/A", Marker());

        var snapshot = registry.Current;
        registry.Set("mesh/Plugins/B", Marker());

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Current.Count);
    }

    [Fact]
    public void ContributionsFoldInOrderOverTheBaseConfiguration()
    {
        // The registry stores transforms; the portal folds them. Pinned here because the fold is
        // what makes a contribution actually reach a hub, and Aggregate's argument order is easy to
        // invert with no compile error — the transforms would just run against the wrong seed.
        var registry = new PortalConfigurationRegistry();
        var applied = new List<string>();

        registry.Set("mesh/Plugins/A", c => { applied.Add("A"); return c; });
        registry.Set("mesh/Plugins/B", c => { applied.Add("B"); return c; });

        var seed = new MessageHubConfiguration(null, new Address("test", "portal-config"));
        var result = ImmutableList<Func<MessageHubConfiguration, MessageHubConfiguration>>.Empty
            .AddRange(registry.Current.Select(contribution => contribution.Configure))
            .Aggregate(seed, (config, transform) => transform(config));

        Assert.Equal(["A", "B"], applied);
        Assert.Same(seed, result);
    }
}
