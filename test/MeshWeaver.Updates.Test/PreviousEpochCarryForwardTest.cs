using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 Scenario 10 — a portal that has NOT rolled across a declared compatibility break (#3583,
/// policy <c>bake-per-live-identity</c>). The publish-bake lane's previous-epoch leg CARRIES FORWARD
/// the previous epoch's sealed module set (maintainer decision: no second build), so what such a
/// portal is handed for its own key is the set its epoch already sealed.
///
/// <para>These pin the portal side of that decision, on the real boot computation
/// (<see cref="ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform"/>): the carried set is
/// ADOPTED by a previous-epoch portal and wins over the image's copy; the same module built for the
/// CURRENT epoch is declined there in favour of the image's copy; and the carried set is declined by
/// a portal that HAS rolled. The last test pins why the lane must refuse a foreign-identity bundle
/// before sealing it (<c>mw-plugin-test module-identity</c>, proven in
/// <c>ModuleIdentityCheckTest</c>): where the image ships no copy, the portal has nothing to prefer
/// and adopts whatever its publication sealed.</para>
/// </summary>
public class PreviousEpochCarryForwardTest
{
    private const string Plugin = "Acme.Widgets";
    private const string ImageBaseline = Plugin + ".dll";
    private static readonly string PreviousEpoch = PlatformCompatibility.KeyOf(3, 1);
    private static readonly string CurrentEpoch = PlatformCompatibility.KeyOf(3, 2);

    private static ModuleActivationEntry Landed(string frameworkIdentity) => new()
    {
        Name = Plugin,
        PackagePath = "Plugins/AcmeWidgets",
        Version = "1.0.0",
        FrameworkMvid = frameworkIdentity,
        Enabled = true,
    };

    /// <summary>One replica's boot decision for the module, on a platform stating
    /// <paramref name="portalIdentity"/>, with the image shipping its own copy unless told not to.</summary>
    private static (EffectiveModule Module, List<string> Skips) Boot(
        string portalIdentity, ModuleActivationEntry landed, bool imageShips = true)
    {
        var skips = new List<string>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform(
            imageShips ? [ImageBaseline] : [],
            new ModuleActivationList { Entries = [landed] },
            _ => null,
            _ => true,
            (module, reason) => skips.Add($"{module}: {reason}"),
            onAdvisory: null,
            [portalIdentity]);
        return (effective.Single(), skips);
    }

    [Fact]
    public void ThePreviousEpochPortal_AdoptsTheCarriedForwardSet()
    {
        var (module, skips) = Boot(PreviousEpoch, Landed(PreviousEpoch));

        module.Landed.Should().NotBeNull(
            "the carried-forward bundle states the previous epoch's key, which is the key this portal "
            + "runs: it is the publication's copy and it wins");
        module.Landed!.FrameworkMvid.Should().Be(PreviousEpoch);
        skips.Should().BeEmpty();
    }

    [Fact]
    public void ThePreviousEpochPortal_DeclinesACurrentEpochBuild_ForTheImageCopy()
    {
        var (module, skips) = Boot(PreviousEpoch, Landed(CurrentEpoch));

        module.Landed.Should().BeNull("bytes built for the current epoch are not this platform's");
        module.PreferImageCopy.Should().BeTrue();
        skips.Should().ContainSingle().Which.Should().Contain("declined");
    }

    [Fact]
    public void ARolledPortal_DeclinesTheCarriedForwardSet()
    {
        var (module, _) = Boot(CurrentEpoch, Landed(PreviousEpoch));

        module.Landed.Should().BeNull(
            "the carried set is the previous epoch's, sealed under its key; a portal on the new key "
            + "never runs it");
    }

    /// <summary>
    /// Why the LANE refuses: with no image copy to prefer, a portal adopts a store copy whatever
    /// identity it states. So a current-epoch build sealed under the previous key would run on
    /// every portal that has not rolled — the gap #3583 named, closed at the producer.
    /// </summary>
    [Fact]
    public void WithNoImageCopy_ThePortalCannotRefuseAForeignBuild_SoTheLaneMust()
    {
        var (module, _) = Boot(PreviousEpoch, Landed(CurrentEpoch), imageShips: false);

        module.Landed.Should().NotBeNull(
            "a store-only module is never declined — the portal has nothing to fall back to");
    }
}
