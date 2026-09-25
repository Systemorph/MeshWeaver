using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Platform/plugin compatibility is a LADDER — the two regressions that would break it, held
/// by a scan</b> (policy <c>platform-backwards-compatibility</c>, <c>Doc/Architecture/PolicyNotProse</c>).
///
/// <list type="number">
/// <item><b>(a) A per-build identity must never DECIDE.</b> Compiled bytes are keyed on the platform
/// compatibility key (<c>FrameworkBuildIdentity.FrameworkVersion</c>, <c>c&lt;major&gt;e&lt;epoch&gt;</c>);
/// the per-build surface/commit identity is PROVENANCE (<c>BuildProvenance</c>,
/// <c>ProducerStatedProvenance</c>, <c>ResolveBuildProvenanceForDirectory</c>,
/// <c>FrameworkIdentity.ReadProvenance</c>). A statement that COMPARES one of those —
/// <c>==</c>, <c>!=</c>, <c>Equals(</c>, <c>Compare(</c> — is an exact-build gate on adoption,
/// loading or rolling, which is precisely the shape that made every platform build miss the whole
/// compiled cache and put a per-identity re-seal on the roll path.</item>
/// <item><b>(d) Plugin load contexts must never hard-link an exact version.</b> A collectible
/// <c>AssemblyLoadContext</c> that compares an <c>AssemblyName.Version</c> by equality refuses a
/// plugin compiled against an older platform build; the rule is <c>PlatformBinding.MayBind</c>
/// (<c>running &gt;= compiledAgainst</c>), and the module link probe must apply that one
/// comparison.</item>
/// </list>
///
/// <para>Comments and strings are masked, so prose naming the members is not a finding. Each scan
/// is proven over a planted tree by the real scanner (negative controls included).</para>
/// </summary>
public class PlatformCompatibilityRatchetGuard
{
    private static readonly ImmutableArray<string> ScannedRoots = ["src", "memex", "tools"];

    private static readonly Regex ProvenanceMember = new(
        @"\b(?:BuildProvenance|ProducerStatedProvenance|ResolveBuildProvenanceForDirectory|ReadProvenance)\b",
        RegexOptions.Compiled);

    private static readonly Regex Comparison = new(
        @"==|!=|\bEquals\s*\(|\bCompare\s*\(|\bCompareTo\s*\(", RegexOptions.Compiled);

    private static readonly Regex LoadContextDeclaration = new(
        @":\s*AssemblyLoadContext\b", RegexOptions.Compiled);

    private static readonly Regex ExactVersionCompare = new(
        @"\.Version\s*(?:==|!=)|\.Version\??\s*\.\s*Equals\s*\(|\bVersion\s*\.\s*Equals\s*\(", RegexOptions.Compiled);

    [Fact]
    public void NoPerBuildIdentityDecidesAdoptionLoadingOrRolling()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots)
            .SelectMany(f => ProvenanceComparisonsIn(File.ReadAllText(f))
                .Select(s => $"  {SourceScan.Relative(root, f)}: {s}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "🚨 A per-build framework identity (BuildProvenance / ProducerStatedProvenance / "
            + "ResolveBuildProvenanceForDirectory / ReadProvenance) is COMPARED here — an exact-build gate "
            + "(policy platform-backwards-compatibility). Compiled bytes are keyed on the compatibility "
            + "key FrameworkBuildIdentity.FrameworkVersion and ranged by PlatformCompatibility.DeclineReason "
            + "(floor/ceiling); a break is DECLARED by bumping the epoch in "
            + "src/MeshWeaver.Compiler/platform-compatibility.json, never gated on a build identity:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void NoPluginLoadContextHardLinksAnExactVersion()
    {
        var root = SourceScan.FindRepoRoot();
        var offenders = SourceScan.SourceFiles(root, ScannedRoots)
            .Where(f => ExactVersionLoadContext(File.ReadAllText(f)))
            .Select(f => "  " + SourceScan.Relative(root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "🚨 An AssemblyLoadContext here compares an assembly Version by EQUALITY — a hard link that "
            + "refuses a plugin compiled against an older platform build. Bind to the running platform "
            + "whenever PlatformBinding.MayBind(compiledAgainst, running) (running >= compiledAgainst); "
            + "a HIGHER requested version is the floor-not-met case, declined loudly:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheModuleLinkProbeAppliesTheOneBindingRule()
    {
        var root = SourceScan.FindRepoRoot();
        var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(
            Path.Combine(root, "src", "MeshWeaver.Mesh.Contract", "ModulePlatformLink.cs")));
        Assert.Contains("PlatformBinding.MayBind(", code);
        Assert.DoesNotMatch(new Regex(@"wantedVersion\s*\.\s*CompareTo\s*\(\s*haveVersion"), code);
    }

    /// <summary>Proven over a planted tree, running the REAL scanners.</summary>
    [Fact]
    public void TheScannersSeeWhatTheyClaimTo()
    {
        // (a)
        Assert.NotEmpty(ProvenanceComparisonsIn(
            "class A { bool M(string x) => string.Equals(x, FrameworkBuildIdentity.BuildProvenance, StringComparison.Ordinal); }"));
        Assert.NotEmpty(ProvenanceComparisonsIn(
            "class B { bool M(string x) { return x == FrameworkBuildIdentity.ProducerStatedProvenance; } }"));
        Assert.NotEmpty(ProvenanceComparisonsIn(
            "class C { void M(string d, string k) { if (FrameworkBuildIdentity.ResolveBuildProvenanceForDirectory(d).Identity != k) Hold(); } }"));
        Assert.Empty(ProvenanceComparisonsIn(
            "class D { void M(ILogger l) { l.LogInformation(\"{P}\", FrameworkBuildIdentity.BuildProvenance); } }"));
        Assert.Empty(ProvenanceComparisonsIn(
            "// string.Equals(x, FrameworkBuildIdentity.BuildProvenance)\nclass E { string s = \"BuildProvenance == x\"; }"));
        Assert.Empty(ProvenanceComparisonsIn(
            "class F { bool M(string x) => string.Equals(x, FrameworkBuildIdentity.FrameworkVersion, StringComparison.Ordinal); }"));

        // (d)
        Assert.True(ExactVersionLoadContext(
            "class G : AssemblyLoadContext { protected override Assembly? Load(AssemblyName n) { if (n.Version == Pinned) return X; return null; } }"));
        Assert.True(ExactVersionLoadContext(
            "class H : AssemblyLoadContext { protected override Assembly? Load(AssemblyName n) => n.Version.Equals(Pinned) ? X : null; }"));
        Assert.False(ExactVersionLoadContext(
            "class I : AssemblyLoadContext { protected override Assembly? Load(AssemblyName n) => Default.LoadFromAssemblyName(n); }"));
        Assert.False(ExactVersionLoadContext(
            "class J { bool M(AssemblyName n) => n.Version == V; }"));
    }

    private static ImmutableArray<string> ProvenanceComparisonsIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var found = ImmutableArray.CreateBuilder<string>();
        foreach (Match m in ProvenanceMember.Matches(code))
        {
            var start = code.LastIndexOfAny([';', '{', '}'], m.Index) + 1;
            var end = code.IndexOfAny([';', '{', '}'], m.Index + m.Length);
            var statement = code[start..(end < 0 ? code.Length : end)];
            if (Comparison.IsMatch(statement))
                found.Add(Regex.Replace(statement.Trim(), @"\s+", " "));
        }
        return found.ToImmutable();
    }

    private static bool ExactVersionLoadContext(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        return LoadContextDeclaration.IsMatch(code) && ExactVersionCompare.IsMatch(code);
    }
}
