#pragma warning disable CS1591

using System;
using System.Linq;
using MeshWeaver.Mesh.Services;
using Orleans;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins that <see cref="PathCacheInvalidatorGrain"/> is implicitly subscribed to the broadcast
/// stream of EVERY <see cref="MeshChangeKind"/>, and that its subscribe loop covers the same set.
/// The publisher (<c>OrleansMeshChangeFeed.BroadcastAsync</c>) writes every kind onto
/// <c>mesh-{kind}</c>; a kind with no subscriber is a cross-silo event that silently reaches no
/// other silo. That is exactly what <c>Updated</c> was until 2026-08-28: a node RETYPE left every
/// other silo's path-resolution cache on the old type, so a recycled hub re-bound to it forever.
/// </summary>
public class PathCacheInvalidatorGrainSubscriptionTest
{
    [Fact]
    public void Grain_IsImplicitlySubscribed_ToEveryMeshChangeKindStream()
    {
        var predicates = typeof(PathCacheInvalidatorGrain)
            .GetCustomAttributes(typeof(ImplicitStreamSubscriptionAttribute), inherit: false)
            .Cast<ImplicitStreamSubscriptionAttribute>()
            .Select(a => a.Predicate)
            .ToList();
        var expected = Enum.GetValues<MeshChangeKind>()
            .Select(PathCacheInvalidatorGrain.StreamNamespaceOf)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        foreach (var ns in expected)
            Assert.True(predicates.Any(p => p.IsMatch(ns)), $"no [ImplicitStreamSubscription] matches '{ns}'");
        Assert.Equal(expected.Count, predicates.Count);
        Assert.Equal(expected, PathCacheInvalidatorGrain.StreamNamespaces.OrderBy(s => s, StringComparer.Ordinal).ToList());
        var declared = expected;
        Assert.Contains("mesh-updated", declared);
    }
}
