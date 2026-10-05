#pragma warning disable CS1591
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 follow-up, as data: a package whose declared <c>requires</c> floor the LOADED
/// dependency does not meet is DECLINED on import — its sources neither move nor compile against a
/// build that lacks what they call. Measured 2026-10-04: Hosting 1.56 (<c>AI@^1.21.0</c>) was
/// imported and compiled against the loaded AI 1.20.4, and every thread start threw
/// <c>MissingMethodException</c>.
/// </summary>
public class ModuleSyncDependencyFloorTest
{
    private const string Running = "3.0.0-ci.9930";

    private static readonly ImmutableDictionary<string, LoadedPackageModule> AiAt1204 =
        ImmutableDictionary<string, LoadedPackageModule>.Empty.Add("AI", new("MeshWeaver.AI", "1.20.4"));

    private static ModuleReading Hosting(params string[] requires)
        => new("Hosting", "", "5e0c0ffee5e0c0ff", null) { Requires = [.. requires] };

    private static ImmutableList<ModuleSyncOutcome> Judge(
        ModuleReading reading, IReadOnlyDictionary<string, LoadedPackageModule> loaded,
        IReadOnlyDictionary<string, string>? held = null)
        => ModuleSyncDecision.DeclineUnmetRequirements(
            ModuleSyncDecision.Decide([reading], held, Running, reconcile: false), [reading], loaded);

    /// <summary>THE repro: AI@^1.21.0 against a loaded AI 1.20.4 is declined, naming both.</summary>
    [Fact]
    public void ARequirementTheLoadedDependencyDoesNotMeet_DeclinesTheModule_NamingBothVersions()
    {
        var outcome = Judge(Hosting("Store@^1.0.0", "AI@^1.21.0"), AiAt1204,
            new Dictionary<string, string> { ["Hosting"] = "1d1d1d1d1d1d1d1d" }).Single();

        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        outcome.UnmetRequirement.Should().Be("AI@^1.21.0");
        outcome.Reason.Should().Contain("AI@^1.21.0").And.Contain("MeshWeaver.AI").And.Contain("1.20.4");
        outcome.HeldVersion.Should().Be("1d1d1d1d1d1d1d1d");
        ModuleSyncDecision.Recorded([outcome])["Hosting"].Should().Be("1d1d1d1d1d1d1d1d",
            "a declined module did not land, so the Space keeps the hash it held — its files stay in the next diff");
    }

    /// <summary>The release: once a satisfying AI is LOADED, the same tree syncs.</summary>
    [Fact]
    public void TheSameTree_Syncs_OnceASatisfyingDependencyIsLoaded()
        => Judge(Hosting("AI@^1.21.0"),
                ImmutableDictionary<string, LoadedPackageModule>.Empty.Add("AI", new("MeshWeaver.AI", "1.21.0")))
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    /// <summary>An unknown loaded version, an uninstalled dependency and an unreadable range are not
    /// judged — the module syncs exactly as before this rule.</summary>
    [Theory]
    [InlineData("AI@^1.21.0", "Store")]
    [InlineData("AI@1.x", "AI")]
    [InlineData("AI", "AI")]
    public void WhatCannotBeJudged_Syncs(string requirement, string loadedPackage)
        => Judge(Hosting(requirement),
                ImmutableDictionary<string, LoadedPackageModule>.Empty.Add(loadedPackage, new("M", "1.20.4")))
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    /// <summary>A platform-floor decline and an unchanged module pass through untouched.</summary>
    [Fact]
    public void OnlyASyncedOutcomeIsJudged()
    {
        var unchanged = Judge(Hosting("AI@^1.21.0"), AiAt1204,
            new Dictionary<string, string> { ["Hosting"] = "5e0c0ffee5e0c0ff" }).Single();
        unchanged.Outcome.Should().Be(ModuleSyncOutcomeKind.Unchanged);
        unchanged.UnmetRequirement.Should().BeNull();
    }

    /// <summary>The reading parses <c>content.requires</c> off the module root's index.json.</summary>
    [Fact]
    public void Read_ParsesTheDeclaredRequirements()
        => ModuleSyncDecision.Read([
                ("manifest.lock", """{"module":"Hosting","moduleVersion":"5e0c0ffee5e0c0ff"}"""),
                ("index.json", """{"content":{"requires":["Store@^1.0.0","AI@^1.21.0"],"minMeshVersion":"3.0.0-ci.9917"}}"""),
            ])
            .Single().Requires.Should().Equal("Store@^1.0.0", "AI@^1.21.0");
}
