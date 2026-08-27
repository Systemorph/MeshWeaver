using System;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the sizing rule for the install-time prebuilt seed bound
/// (<see cref="PackageInstaller.SeedBound"/>).
///
/// <para>🚨 The rule this protects: <b>a bound on the cheap path must never be tighter than the
/// work it bounds.</b> The seed underneath is a sequential <c>Concat</c> with a 30 s budget PER
/// assembly, so a flat cap (it was 60 s) expired while the inner work was still legitimately inside
/// its own budget — and <c>Timeout</c> abandons the result, not the work, so the install compiled
/// the very types the seed then finished delivering. Measured 2026-08-27, Education e2e shard 1:
/// Store in Roslyn at 06:38, "adoption attempt failed" at 06:51, tally right behind it reporting 27
/// assemblies backed. A cap that fires makes the system do both.</para>
/// </summary>
public class InstallSeedBoundTest
{
    [Fact]
    public void Bound_GrowsWithTheTypeCount_AtTheInnerPerAssemblyBudget()
    {
        // Each extra type adds exactly the inner seed's per-assembly budget, so the outer bound can
        // never expire while the inner Concat is still within its own.
        var one = PackageInstaller.SeedBound(1);
        var two = PackageInstaller.SeedBound(2);
        Assert.Equal(TimeSpan.FromSeconds(30), two - one);
    }

    [Fact]
    public void Bound_ForStoreSizedPackage_ExceedsTheOldFlatCap()
    {
        // Store ships 17 NodeType assemblies. Under the old flat 60 s cap it could not be seeded
        // even in principle: 17 × 30 s of allowed inner work against a 60 s outer ceiling.
        var store = PackageInstaller.SeedBound(17);
        Assert.True(store > TimeSpan.FromSeconds(60),
            $"a 17-type package must be allowed more than the old flat 60 s cap, got {store}");
        Assert.True(store >= TimeSpan.FromSeconds(30) * 17,
            $"the bound must cover the inner per-assembly budget summed over every type, got {store}");
    }

    [Fact]
    public void Bound_NeverBelowTheFloor_EvenForNoTypes()
    {
        // The enumeration and bundle reads cost something before the first assembly is touched;
        // zero (or a defensive negative) types still gets the floor, never zero or negative.
        Assert.Equal(PackageInstaller.SeedBound(0), PackageInstaller.SeedBound(-5));
        Assert.True(PackageInstaller.SeedBound(0) > TimeSpan.Zero);
    }
}
