#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A package reason never says the roll is held</b> — policy
/// <c>platform-rolls-independently-of-packages</c>.
///
/// <para>A package availability reason is carried VERBATIM into a roll advisory
/// (<c>UpdatabilityVerdict.ForPlatformRoll()</c>), and the Updates tab shows it while the roll
/// proceeds. A reason that ends "nothing rolls" or "not clearance to proceed" therefore tells the
/// operator the opposite of what is happening. Every reason sentence these files build is checked
/// here, so a new branch cannot reintroduce the wording: the empty-denominator refusal
/// (<c>ReleaseAvailabilityService</c>), every <c>PackageAvailability</c> reason
/// (<c>ReleaseAvailability</c>) and the unreadable-root refusal (<c>PublishedBundleCatalogue</c>).</para>
///
/// <para>The sentences are split across concatenated literals, so the check reads the string
/// literals of each file (comments excluded) joined in source order, which is how the sentence is
/// assembled at run time.</para>
/// </summary>
public class PackageReasonsNeverSayTheRollIsHeldTest
{
    private static readonly string[] ReasonSources =
    [
        Path.Combine("src", "MeshWeaver.PluginCatalog", "ReleaseAvailability.cs"),
        Path.Combine("src", "MeshWeaver.PluginCatalog", "PublishedBundleCatalogue.cs"),
        Path.Combine("memex", "Memex.Portal.Shared", "SelfUpdate", "ReleaseAvailabilityService.cs"),
    ];

    /// <summary>Phrases that state a roll hold. A package reason states a PACKAGE result.</summary>
    private static readonly string[] Forbidden =
    [
        "roll is held",
        "nothing rolls",
        "not clearance",
        "clearance to roll",
        "clearance to proceed",
        "holds the roll",
        "the roll stops",
    ];

    private static readonly Regex Literal = new(@"\$?@?""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    [Fact]
    public void NoPackageReasonSentence_StatesARollHold()
    {
        var root = RepoRoot();
        foreach (var relative in ReasonSources)
        {
            var text = LiteralText(File.ReadAllText(Path.Combine(root, relative)));
            Assert.False(string.IsNullOrEmpty(text), $"{relative} builds reason sentences from string literals");
            foreach (var phrase in Forbidden)
                text.Should().NotContain(phrase,
                    $"{relative} builds a package reason that is carried verbatim into a roll advisory, "
                    + "and the platform rolls independently of packages");
        }
    }

    /// <summary>The negative control: the extraction finds a forbidden phrase split across two
    /// concatenated literals, exactly the shape the reasons were written in.</summary>
    [Fact]
    public void TheCheck_FindsAPhraseSplitAcrossConcatenatedLiterals()
    {
        const string source = """
            // a comment saying the roll is held is not a reason
            return "cannot determine availability, which is not "
                   + "clearance to proceed";
            """;
        var text = LiteralText(source);
        text.Should().Contain("not clearance to proceed");
        text.Should().NotContain("roll is held", "comments are not reasons");
    }

    private static string LiteralText(string source)
    {
        var sb = new StringBuilder();
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                continue;
            foreach (Match m in Literal.Matches(line))
                sb.Append(m.Groups[1].Value);
        }
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.SkipWhen(dir is null, "repository tree not reachable — source guard runs in-repo only");
        return dir!.FullName;
    }
}
