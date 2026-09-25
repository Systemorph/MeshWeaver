using System.Collections.Immutable;
using System.IO;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>A break is acceptable only when it is DECLARED, and a declaration only when there is a
/// break</b> — the rule the static compatibility gate (<c>mw-plugin-test platform-link</c>) holds a
/// platform pull request to (policy <c>platform-backwards-compatibility</c>). Never implied, never
/// silent: every arm below is a distinct verdict, and each red names what to do.
/// </summary>
public class PlatformLinkDecisionTest
{
    private const string Removed = "MeshWeaver.Mesh.MeshNode::Frobnicate [instance Default`0 System.Void ()] (MeshWeaver.Mesh.Contract)";

    private static PlatformLink.ModuleResult Clean() =>
        new("MeshWeaver.Plugin.AI.1.0.0.module.nupkg",
            new ModuleLinkVerdict(ModuleLinkState.Linkable, "MeshWeaver.AI", [], 10, ["MeshWeaver.Mesh.Contract"], [])
            { CheckedMemberReferences = 20 });

    private static PlatformLink.ModuleResult Broken() =>
        new("MeshWeaver.Plugin.AI.1.0.0.module.nupkg",
            new ModuleLinkVerdict(ModuleLinkState.Unlinkable, "MeshWeaver.AI", [], 10, ["MeshWeaver.Mesh.Contract"], [])
            { CheckedMemberReferences = 20, MissingMembers = [Removed] });

    private static PlatformLink.DeclaredBreak Declares(int epoch, params string[] members) =>
        new(epoch, [.. members.Select(m => ("MeshWeaver.Mesh.Contract", m))]);

    /// <summary>Nothing broke, nothing declared: green.</summary>
    [Fact]
    public void NoBreak_NoDeclaration_IsGreen() =>
        Assert.True(PlatformLink.Decide([Clean()], 1, 1, []).Green);

    /// <summary>🚨 A break with the epoch unchanged: red, naming the member and the three remedies.</summary>
    [Fact]
    public void AnUndeclaredBreak_IsRed_NamingTheRemedies()
    {
        var decision = PlatformLink.Decide([Broken()], 1, 1, []);

        Assert.False(decision.Green);
        Assert.Contains(decision.Findings, f => f.Contains("Frobnicate", StringComparison.Ordinal));
        Assert.Contains(decision.Findings, f => f.Contains("[Obsolete] forwarder", StringComparison.Ordinal)
                                                && f.Contains("platform-compatibility.json", StringComparison.Ordinal));
    }

    /// <summary>A break whose pull request moves the epoch AND lists the member: green, and the log
    /// says which plugin must rebuild.</summary>
    [Fact]
    public void ADeclaredBreak_WithTheEpochMovedAndTheMemberListed_IsGreen()
    {
        var decision = PlatformLink.Decide([Broken()], 1, 2, [Declares(2, "MeshWeaver.Mesh.MeshNode::Frobnicate")]);

        Assert.True(decision.Green, string.Join("\n", decision.Findings));
        Assert.Contains(decision.Findings, f => f.Contains("DECLARED break under epoch 2", StringComparison.Ordinal));
    }

    /// <summary>The epoch moved, but the declaration names something else: red — every broken member
    /// must be declared.</summary>
    [Fact]
    public void ADeclarationThatMissesTheBrokenMember_IsRed()
    {
        var decision = PlatformLink.Decide([Broken()], 1, 2, [Declares(2, "MeshWeaver.Mesh.MeshNode::SomethingElse")]);

        Assert.False(decision.Green);
        Assert.Contains(decision.Findings, f => f.Contains("NOT in its declaration", StringComparison.Ordinal));
    }

    /// <summary>A declaration under an OLDER epoch does not cover a break the current bump introduces.</summary>
    [Fact]
    public void ADeclarationUnderAnotherEpoch_DoesNotCoverTheBreak() =>
        Assert.False(PlatformLink.Decide([Broken()], 1, 2, [Declares(1, "MeshWeaver.Mesh.MeshNode::Frobnicate")]).Green);

    /// <summary>🚨 No gratuitous epoch bumps: the epoch moved and nothing deployed breaks — red.</summary>
    [Fact]
    public void AnEpochBumpWithNoBreak_IsRed()
    {
        var decision = PlatformLink.Decide([Clean()], 1, 2, [Declares(2, "MeshWeaver.Mesh.MeshNode::Frobnicate")]);

        Assert.False(decision.Green);
        Assert.Contains(decision.Findings, f => f.Contains("gratuitous", StringComparison.Ordinal));
    }

    /// <summary>An epoch only ever increases.</summary>
    [Fact]
    public void AnEpochMovedBackwards_IsRed() =>
        Assert.False(PlatformLink.Decide([Clean()], 2, 1, []).Green);

    /// <summary>A verdict over no plugins is not a verdict.</summary>
    [Fact]
    public void NothingChecked_IsRed() =>
        Assert.False(PlatformLink.Decide([], 1, 1, []).Green);

    /// <summary>A binding conflict (the FileLoadException shape) is never "declarable" as a member
    /// break — the epoch moving does not excuse it.</summary>
    [Fact]
    public void ABindingConflict_IsRed_EvenWithTheEpochMoved()
    {
        var conflict = new PlatformLink.ModuleResult("x.nupkg",
            new ModuleLinkVerdict(ModuleLinkState.BindingConflict, "MeshWeaver.AI", [], 1, [], [])
            { Conflicts = [new AssemblyBindingConflict("YamlDotNet", "18.1.0.0", "16.3.0.0")] });

        Assert.False(PlatformLink.Decide([conflict], 1, 2, [Declares(2, "YamlDotNet")]).Green);
    }

    /// <summary>The declaration file is read strictly: a missing file is "no declaration yet", a
    /// malformed one throws rather than reading as "no breaks".</summary>
    [Fact]
    public void TheDeclarationFile_IsReadStrictly()
    {
        var path = Path.Combine(Path.GetTempPath(), "mw-decl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal((int?)null, PlatformLink.ReadDeclaration(path).Epoch);

            File.WriteAllText(path, """
                { "epoch": 2, "breaks": [ { "epoch": 2, "previousEpochCeiling": "3.0.0-ci.9400", "reason": "r",
                  "declaredIn": "#1", "members": [ { "assembly": "MeshWeaver.Mesh.Contract", "member": "MeshWeaver.Mesh.MeshNode::Frobnicate" } ],
                  "affected": ["MeshWeaver.AI"] } ] }
                """);
            var (epoch, breaks) = PlatformLink.ReadDeclaration(path);
            Assert.Equal(2, epoch);
            Assert.Equal("MeshWeaver.Mesh.MeshNode::Frobnicate", Assert.Single(Assert.Single(breaks).Members).Member);

            File.WriteAllText(path, """{ "breaks": [] }""");
            Assert.Throws<InvalidDataException>(() => PlatformLink.ReadDeclaration(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
