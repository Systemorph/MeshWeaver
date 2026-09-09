#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An install record naming a package NO PUBLISHER PRODUCES must be NAMED, and must never
/// hold (#3706).</b>
///
/// <para>memex's install records carried <c>Plugins/Agent</c>, <c>Plugins/Skill</c> and
/// <c>Plugins/PlatformUI</c> after the packages themselves had gone — Agent and Skill deliberately
/// (<c>MeshWeaver.Plugins@7afbd745</c>, "remove agents and skill package and serve from the main ai
/// package"; the AI engine's <c>BuiltInAgentProvider</c>/<c>BuiltInSkillProvider</c> are the live
/// master). The issue's premise was that their retired floors held every self-update for ever.</para>
///
/// <para><b>Half of that premise was already false, and the other half is what this fixes.</b> The
/// floors stopped deciding anything at #3648 and a missing bake stopped holding at #3651, so
/// nothing was actually blocking — the sentence quoted as the blocker came from a
/// <c>heldReason</c> written once, 36 hours before it was read, by a path that no longer runs.
/// Not holding was right. Saying NOTHING was not: an operator had no way to learn the records were
/// stale except by reading that frozen field and reaching the wrong conclusion.</para>
///
/// <para>These cases pin both halves at once. Asserting only "it does not hold" would pass against
/// a gate that had never heard of the package — which is precisely the state that made #3706 hard
/// to diagnose — so every case that asserts <c>IsUpdatable</c> also asserts that the package is
/// NAMED, and <see cref="AnUnreadableModuleSet_SaysNothing_BecauseNothingWasLookedFor"/> is the
/// control that stops the advisory degrading into an unconditional sentence.</para>
/// </summary>
public class OrphanedPackageRecordTest
{
    private const string Identity = "s8055";
    private static readonly ReleaseTarget Target = new("3.0.0-ci.8195", Identity);

    /// <summary>A module set that WAS read completely and carries only <paramref name="modules"/>.</summary>
    private static SealedModuleSet SetCarrying(params string[] modules) =>
        new(modules.ToImmutableDictionary(m => m, m => "mvid:" + new string('a', 32)), [], null);

    private static ReleaseArtifacts Artifacts(SealedModuleSet? modules, params string[] bundles) =>
        ReleaseArtifacts.Of([.. bundles]) with { Modules = modules };

    /// <summary>The package this instance can never obtain: its module is not in the target's
    /// sealed set, and no generation of it is landed here.</summary>
    private static RequiredPackage Orphan(string name) =>
        new(name, name, MinMeshVersion: "3.0.0", HasContent: false) { ModuleName = "MeshWeaver." + name };

    [Fact]
    public void APackageNoPublisherProduces_IsNamed_AndDoesNotHold()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [Orphan("Agent"), Orphan("Skill"), Orphan("PlatformUI")],
            Artifacts(SetCarrying("MeshWeaver.AI")));

        // 🚨 Not holding is the point: a roll cannot conjure a build nobody publishes, so waiting
        // for one waits for ever — the shape #3706 is named after.
        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Null(verdict.HoldReason);
        Assert.Empty(verdict.Blockers);
        Assert.All(verdict.Packages, p => Assert.Equal(PackageAvailabilityKind.Available, p.Kind));

        // 🚨 …and being SILENT about it was the other half of the defect. One orphan advisory per
        // record, naming the package AND the module, so the operator can act on the record itself.
        //
        // Six advisories, not three: each record also declares a 3.0.0 floor the ci target does not
        // satisfy, and that floor sentence rides BESIDE the orphan one. Asserted deliberately —
        // the two answer different questions ("this release is older than the module asks for" vs
        // "nobody publishes this module at all"), and a fix that swallowed the floor advisory while
        // adding the orphan one would be a regression of #3648 that a total-count assertion of 3
        // would have called a pass.
        Assert.Equal(6, verdict.Advisories.Length);
        var orphanAdvisories = verdict.Advisories
            .Where(a => a.Contains("no publisher produces it", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, orphanAdvisories.Length);
        foreach (var name in new[] { "Agent", "Skill", "PlatformUI" })
        {
            var advisory = Assert.Single(
                orphanAdvisories, a => a.StartsWith(name + ":", StringComparison.Ordinal));
            Assert.Contains("MeshWeaver." + name, advisory, StringComparison.Ordinal);
            // The floor sentence is still there for the same package, untouched.
            Assert.Contains(
                verdict.Advisories,
                a => a.StartsWith(name + ":", StringComparison.Ordinal)
                     && a.Contains("3.0.0", StringComparison.Ordinal)
                     && !a.Contains("no publisher produces it", StringComparison.Ordinal));
            Assert.Contains("no publisher produces it", advisory, StringComparison.Ordinal);
            Assert.Contains("never a hold", advisory, StringComparison.Ordinal);
            // The remedy is on the record, not on the roll — the actionable half.
            Assert.Contains("remove its install record", advisory, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 🚨 THE CONTROL that keeps the advisory honest. A module set that could not be read means the
    /// module was never LOOKED for, and "we did not look" must not be worded as "it does not
    /// exist" — the conflation #1754 forbids one severity up. Without this case the advisory could
    /// degrade into an unconditional sentence and still pass everything above.
    /// </summary>
    [Fact]
    public void AnUnreadableModuleSet_SaysNothing_BecauseNothingWasLookedFor()
    {
        var refused = ReleaseAvailability.IsUpdatable(
            Target, [Orphan("Agent")],
            Artifacts(new SealedModuleSet(ImmutableDictionary<string, string>.Empty, [], "a torn module index")));

        Assert.True(refused.IsUpdatable, refused.HoldReason);
        Assert.DoesNotContain(refused.Advisories, a => a.Contains("no publisher", StringComparison.Ordinal));

        var unobserved = ReleaseAvailability.IsUpdatable(Target, [Orphan("Agent")], Artifacts(null));

        Assert.True(unobserved.IsUpdatable, unobserved.HoldReason);
        Assert.DoesNotContain(unobserved.Advisories, a => a.Contains("no publisher", StringComparison.Ordinal));
    }

    /// <summary>
    /// A package the target DOES carry is not an orphan, however retired its declared floor looks.
    /// This is the case the advisory must not swallow: "3.0.0 floor on a ci target" is the string
    /// comparison that declined every candidate on every portal on 2026-09-07 (#3648), and it must
    /// stay a floor advisory rather than becoming a second, wronger sentence.
    /// </summary>
    [Fact]
    public void APackageTheTargetCarries_GetsNoOrphanAdvisory()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [new RequiredPackage("AI", "AI", MinMeshVersion: "3.0.0", HasContent: false) { ModuleName = "MeshWeaver.AI" }],
            Artifacts(SetCarrying("MeshWeaver.AI")));

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.DoesNotContain(verdict.Advisories, a => a.Contains("no publisher", StringComparison.Ordinal));
        // The floor still rides beside it, unchanged by this work.
        Assert.Contains(verdict.Advisories, a => a.Contains("3.0.0", StringComparison.Ordinal));
    }

    /// <summary>
    /// A content-only record has no module to look for, so it takes the bake path and its wording —
    /// "would recompile at boot" — rather than the orphan sentence. Two different absences, two
    /// different remedies; conflating them would tell an operator to delete a record that is fine.
    /// </summary>
    [Fact]
    public void AContentOnlyRecordWithNoBake_KeepsTheBakeWording()
    {
        var verdict = ReleaseAvailability.IsUpdatable(
            Target,
            [new RequiredPackage("Store", "Store", null, HasContent: true)],
            Artifacts(SetCarrying("MeshWeaver.AI")));

        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Equal(["Store"], verdict.BootCompiles);
        Assert.DoesNotContain(verdict.Advisories, a => a.Contains("no publisher", StringComparison.Ordinal));
        Assert.Contains(verdict.Advisories, a => a.Contains("would recompile at boot", StringComparison.Ordinal));
    }

    /// <summary>
    /// A landed generation is NOT an orphan even when the target publishes no build of it: that is
    /// the module lane's own case, measured on the bytes by the link probe
    /// (<c>ModuleLinkState</c>), and it may legitimately end in a HOLD. The orphan advisory must
    /// not intercept it — the two are told apart by whether anything is landed at all.
    /// </summary>
    [Fact]
    public void ALandedModuleTheTargetDoesNotPublish_StaysWithTheLinkProbe()
    {
        var landed = new RequiredPackage("Edu", "Edu", null, HasContent: false)
        {
            ModuleName = "MeshWeaver.Edu",
            LandedModulePath = "/app/modules/MeshWeaver.Edu@7/MeshWeaver.Edu.dll",
        };

        var verdict = ReleaseAvailability.IsUpdatable(Target, [landed], Artifacts(SetCarrying("MeshWeaver.AI")));

        Assert.DoesNotContain(verdict.Advisories, a => a.Contains("no publisher", StringComparison.Ordinal));
        // Unmeasured here, so the link lane reports its own "could not be determined" and does not hold.
        Assert.True(verdict.IsUpdatable, verdict.HoldReason);
        Assert.Contains(verdict.Advisories, a => a.Contains("could not be determined", StringComparison.Ordinal));
    }
}
