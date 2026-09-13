// <meshweaver>
// Id: Testing/CompilerPipeline/ModuleVersionCompatibilityTest
// DisplayName: Testing/CompilerPipeline/ModuleVersionCompatibilityTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.Mesh.Services;

/// <summary>
/// 🚨 The module-version compatibility rule must never read a CONTENT HASH as a SemVer major.
///
/// <para>Measured on memex 2026-09-12: a bundle baked without a released version carried
/// <c>manifest.lock</c>'s content hash (<c>221c6c286785ddf2</c>) as its module version; the root's
/// <c>CurrentModuleVersion</c> was <c>1.10</c>. <see cref="ModuleVersionCompatibility.MajorOf"/>
/// read the hash's leading digits as major 221, classified the pair INCOMPATIBLE, and the seeder
/// refused the CI bundle for the running framework identity — whose source fingerprint matched the
/// live sources — so every instance of <c>Store/Plugin</c> went through a Roslyn compile it did
/// not need. Ten of sixteen hex hashes start with a digit: the refusal was the common case.</para>
///
/// <para>A hash is UNKNOWN: nothing was compared, nothing is refused. Only a version-shaped value
/// yields a major, and only two version-shaped values whose majors differ refuse.</para>
/// </summary>
public class ModuleVersionCompatibilityTest
{
    [MeshTheory]
    [MeshInlineData("221c6c286785ddf2")]   // the memex bundle: digits, then hex letters
    [MeshInlineData("73eff86754a63ac9")]   // the previous bundle on both portals
    [MeshInlineData("3fa590ff0719c04f")]   // this repo's Store/manifest.lock moduleVersion
    [MeshInlineData("deadbeef")]
    [MeshInlineData("12abc")]
    [MeshInlineData("v9x")]
    public void AContentHash_HasNoMajor(string hash)
        => Assert.Null(ModuleVersionCompatibility.MajorOf(hash));

    [MeshTheory]
    [MeshInlineData("1.10", 1)]
    [MeshInlineData("1.9.10", 1)]
    [MeshInlineData("2", 2)]
    [MeshInlineData("v3.0.0", 3)]
    [MeshInlineData("3.0.0-ci.8399", 3)]
    [MeshInlineData("4.1.0+sha", 4)]
    [MeshInlineData(" 12.0 ", 12)]
    public void AVersionShapedValue_YieldsItsMajor(string version, int major)
        => Assert.Equal(major, ModuleVersionCompatibility.MajorOf(version));

    /// <summary>THE regression: the pair that refused Store/Plugin's CI bundle must be UNKNOWN.</summary>
    [MeshFact]
    public void AHashAgainstASemVer_IsUnknown_NeverIncompatible()
    {
        Assert.Equal(ModuleVersionVerdict.Unknown,
            ModuleVersionCompatibility.Classify("221c6c286785ddf2", "1.10"));
        Assert.False(ModuleVersionCompatibility.Refuses("221c6c286785ddf2", "1.10"),
            "a bundle whose module version is a content hash was never compared to anything, "
            + "so it cannot have declared a MAJOR bump — refusing it is what sent a whole type "
            + "through Roslyn on memex 2026-09-12");
    }

    [MeshFact]
    public void ASemVerAgainstAHash_IsUnknownToo()
        => Assert.Equal(ModuleVersionVerdict.Unknown,
            ModuleVersionCompatibility.Classify("1.10", "221c6c286785ddf2"));

    [MeshFact]
    public void SameMajor_IsCompatible()
        => Assert.Equal(ModuleVersionVerdict.Compatible,
            ModuleVersionCompatibility.Classify("1.9.10", "1.10"));

    [MeshFact]
    public void ADeclaredMajorBump_StillRefuses()
    {
        Assert.Equal(ModuleVersionVerdict.Incompatible,
            ModuleVersionCompatibility.Classify("2.0.0", "1.10"));
        Assert.True(ModuleVersionCompatibility.Refuses("2.0.0", "1.10"),
            "the ONE refusal — a real MAJOR bump between two real versions — is unchanged");
    }

    [MeshTheory]
    [MeshInlineData(null, "1.10")]
    [MeshInlineData("1.10", null)]
    [MeshInlineData("", "1.10")]
    [MeshInlineData("   ", "1.10")]
    public void AMissingSide_IsUnknown(string? adopted, string? current)
        => Assert.Equal(ModuleVersionVerdict.Unknown,
            ModuleVersionCompatibility.Classify(adopted, current));
}
