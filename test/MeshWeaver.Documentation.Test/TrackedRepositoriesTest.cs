using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The identity half of the partition-ownership seam — MeshWeaver#4625, the check that a PROVEN
/// boot install is writing the partition's OWN repository.
///
/// <para>🚨 The most important case in this file is <see cref="TheFleetsNormalShape_DoesNotHold"/>.
/// #4625 stayed open because holding every proven install would take #4259's lane offline, so a
/// gate that cannot tell the fleet's ordinary shape from a mismatch is worse than no gate. Every
/// "no hold" case below is therefore an assertion about an OUTAGE that must not happen, not a
/// convenience.</para>
/// </summary>
public class TrackedRepositoriesTest
{
    private const string Plugins = "https://github.com/Systemorph/MeshWeaver.Plugins";

    [Fact]
    public void TheFleetsNormalShape_DoesNotHold()
    {
        // `Hosting` is installed from MeshWeaver.Plugins' `Hosting` folder and synced from the same
        // repository's `Hosting` subdirectory — the shape every instance runs.
        var tracked = TrackedRepositories.Of([TrackedRepositories.Normalize(Plugins, "Hosting")]);
        var candidate = TrackedRepositories.Normalize(Plugins, "Hosting");

        tracked.DefinitelyDisagreesWith(candidate).Should().BeFalse(
            "holding here would stop the lane #4259 built, on every instance in the fleet");
    }

    [Fact]
    public void ADifferentRepository_Disagrees()
        => TrackedRepositories.Of([TrackedRepositories.Normalize(Plugins, "Hosting")])
            .DefinitelyDisagreesWith(
                TrackedRepositories.Normalize("https://github.com/Systemorph/Example", "Hosting"))
            .Should().BeTrue("two pinned writers of two repositories still land two trees");

    /// <summary>🚨 The SUBDIRECTORY is part of the identity — #4625 names it as one of its two
    /// shapes, and it is the one a slug-only comparison would miss.</summary>
    [Fact]
    public void ADifferentSubdirectoryOfTheSameRepository_Disagrees()
        => TrackedRepositories.Of([TrackedRepositories.Normalize(Plugins, "Hosting")])
            .DefinitelyDisagreesWith(TrackedRepositories.Normalize(Plugins, "Store"))
            .Should().BeTrue("two folders of one repository are two different trees");

    [Fact]
    public void AnUnknownReading_NeverHolds()
        => TrackedRepositories.Unknown
            .DefinitelyDisagreesWith(TrackedRepositories.Normalize(Plugins, "Hosting"))
            .Should().BeFalse(
                "'unknown' is the DEFAULT for every provider that has not implemented the seam — "
                + "holding on it would hold the fleet");

    [Fact]
    public void AKnownReadingThatTracksNothing_NeverHolds()
        => TrackedRepositories.Of([])
            .DefinitelyDisagreesWith(TrackedRepositories.Normalize(Plugins, "Hosting"))
            .Should().BeFalse("nothing imports here, so nothing can disagree");

    [Fact]
    public void AnUnnameableCandidate_NeverHolds()
        => TrackedRepositories.Of([TrackedRepositories.Normalize(Plugins, "Hosting")])
            .DefinitelyDisagreesWith(TrackedRepositories.Normalize("a-registry-with-no-repo"))
            .Should().BeFalse(
                "a registry source has no repository to compare — that residue is gate 1c's, and "
                + "holding it here would hold it twice for one reason");

    /// <summary>One matching identity among several settles it — agreement is decisive, the mirror
    /// of the bit's "a positive is decisive".</summary>
    [Fact]
    public void OneMatchingSourceAmongSeveral_DoesNotHold()
        => TrackedRepositories.Of([
                TrackedRepositories.Normalize("https://github.com/Systemorph/Other", "X"),
                TrackedRepositories.Normalize(Plugins, "Hosting"),
            ])
            .DefinitelyDisagreesWith(TrackedRepositories.Normalize(Plugins, "Hosting"))
            .Should().BeFalse();

    [Theory]
    [InlineData("https://github.com/Systemorph/MeshWeaver.Plugins", "systemorph/meshweaver.plugins")]
    [InlineData("https://github.com/Systemorph/MeshWeaver.Plugins.git", "systemorph/meshweaver.plugins")]
    [InlineData("https://github.com/Systemorph/MeshWeaver.Plugins/", "systemorph/meshweaver.plugins")]
    [InlineData("git@github.com:Systemorph/MeshWeaver.Plugins.git", "systemorph/meshweaver.plugins")]
    [InlineData("Systemorph/MeshWeaver.Plugins", "systemorph/meshweaver.plugins")]
    [InlineData("SYSTEMORPH/meshweaver.PLUGINS", "systemorph/meshweaver.plugins")]
    public void EverySpellingOfOneRepository_NormalizesToOneIdentity(string input, string expected)
        => TrackedRepositories.Normalize(input).Should().Be(expected,
            "a config may carry a URL, an scp remote or a slug, and two spellings of one repository "
            + "must never read as two repositories — that would hold an install for nothing");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-slash")]
    public void WhatCannotBeParsed_IsEmpty_NeverAGuess(string? input)
        => TrackedRepositories.Normalize(input).Should().BeEmpty();

    [Fact]
    public void TheSubdirectoryIsNormalizedToo_SoSlashesAndCaseDoNotSplitOneTree()
    {
        var a = TrackedRepositories.Normalize(Plugins, "/Hosting/");
        var b = TrackedRepositories.Normalize(Plugins, "hosting");
        a.Should().Be(b).And.Be("systemorph/meshweaver.plugins#hosting");
    }

    /// <summary>Known-empty and unknown are different values, and the record says so — the same
    /// distinction #4620 needed one layer down.</summary>
    [Fact]
    public void KnownEmpty_AndUnknown_AreNotTheSameReading()
    {
        TrackedRepositories.Unknown.Known.Should().BeFalse();
        TrackedRepositories.Of([]).Known.Should().BeTrue();
    }
}
