using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b><c>-p:Version=</c> must not reach a COMPILED attribute</b> (#3022) — the invariant the
/// framework build identity, and therefore the whole prebuilt-NodeType delivery lane, rests on.
///
/// <para><b>What went wrong.</b> <c>Directory.Build.props</c> stated the rule in prose ("$(Version)
/// — the run-numbered string — feeds NuGet package versions and Docker image tags ONLY. It must NOT
/// reach any COMPILED attribute") and then broke it in the very next line: the PropertyGroup that
/// derives <c>InformationalVersion</c>, <c>AssemblyVersion</c> and <c>FileVersion</c> from
/// <c>$(PlatformVersion)</c> carried <c>Condition="'$(Version)' == ''"</c>. Supplying
/// <c>-p:Version=</c> made <c>$(Version)</c> a global property, skipped the whole group, and left
/// the three attributes to the SDK's defaults — which derive them from <c>$(Version)</c>, the
/// run-numbered string.</para>
///
/// <para><b>What it cost.</b> CD's <c>portal-image</c> leg passes <c>-p:Version=</c> (so the image
/// reports the build it is running, #2555); <c>plugin-test-image</c> does not. Same commit, same
/// runner, same architecture — and every core assembly inside <c>memex-portal-ai</c> carried
/// <c>AssemblyInformationalVersion 3.0.0-rc9.ci.&lt;run&gt;+&lt;sha&gt;</c> while the same assembly
/// inside <c>mw-plugin-test</c> carried <c>3.0.0-rc9+&lt;sha&gt;</c>. An assembly attribute is part
/// of the REFERENCE assembly, which is exactly what <c>meshweaver-surface.manifest</c> hashes, so
/// all 25 shared canonical surface hashes differed and the two images resolved different framework
/// identities (<c>sb0f6a11…</c> vs <c>s6dd7f50…</c> on run 33573271760). Every bake was published
/// to an address no portal asks for: INERT, #1814's shape, pods recompiling the whole mesh at
/// boot.</para>
///
/// <para><b>Why a guard and not just the fix.</b> The defect is invisible to every build: both
/// publishes succeed, both images ship, and only a hash nobody compares is wrong. CD's
/// <c>mw-plugin-test framework-identity … --expect …</c> step does catch it — but only after the
/// image set has been built and promoted, at the end of a 25-minute run, and only for the pair CD
/// happens to compare. This asserts the property itself, in seconds, on every PR.</para>
///
/// <para>See <c>Doc/Architecture/BakeIdentityMismatch</c> for the full reconstruction.</para>
/// </summary>
public class CompiledVersionAttributesIgnoreVersionOverrideGuard
{
    /// <summary>
    /// The project evaluated. Any project inheriting the root <c>Directory.Build.props</c> would
    /// do; this one is picked because its assembly is part of the canonical content surface (see
    /// <see cref="ProbedAssemblyIsPartOfTheContentSurface"/>), so the guard is measuring a build
    /// whose attributes really do feed the framework identity.
    /// </summary>
    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    private const string ProbeAssembly = "MeshWeaver.ShortGuid";

    /// <summary>
    /// A version that could never be produced by <c>Directory.Build.props</c> itself, so its
    /// appearance in a compiled attribute is unambiguous evidence that the override leaked.
    /// </summary>
    private const string ProbeVersion = "9.9.9-guard.ci.424242";

    /// <summary>The three properties that become attributes inside the emitted (and reference) assembly.</summary>
    private static readonly string[] CompiledAttributes =
        ["InformationalVersion", "AssemblyVersion", "FileVersion"];

    /// <summary>
    /// The measurement: evaluate the same project twice — once bare, once with
    /// <c>-p:Version=</c> — and require the compiled attributes to be identical.
    /// </summary>
    [Fact]
    public void SupplyingAVersionDoesNotChangeAnyCompiledVersionAttribute()
    {
        var bare = Evaluate();
        var overridden = Evaluate($"-p:Version={ProbeVersion}");

        // 🚨 Non-empty in BOTH, asserted first. The regression this guard exists for evaluated the
        // three properties to the EMPTY STRING (the SDK fills them in a later target, from
        // $(Version)), so a bare equality check would have compared "" with "" and passed while
        // shipping the defect.
        foreach (var name in CompiledAttributes)
        {
            Assert.False(string.IsNullOrWhiteSpace(bare[name]),
                $"{name} evaluated empty for {ProbeProject} with no -p:Version. The root "
                + "Directory.Build.props no longer sets it, so the SDK will derive it from "
                + "$(Version) — the run-numbered string — and two publishes of one commit will "
                + "fork the framework identity (#3022).");
            Assert.False(string.IsNullOrWhiteSpace(overridden[name]),
                $"{name} evaluated empty for {ProbeProject} once -p:Version was supplied. That is "
                + "the exact #3022 regression: the PropertyGroup deriving the compiled version "
                + "attributes is guarded on '$(Version)' == '' again, so an explicit -p:Version "
                + "hands them to the SDK's $(Version)-derived defaults.");
        }

        var drifted = CompiledAttributes
            .Where(name => !string.Equals(bare[name], overridden[name], StringComparison.Ordinal))
            .Select(name => $"{name}: '{bare[name]}' -> '{overridden[name]}'")
            .ToList();

        Assert.True(drifted.Count == 0,
            "Supplying -p:Version= changed a COMPILED version attribute of " + ProbeProject + ":\n  "
            + string.Join("\n  ", drifted)
            + "\n\nThese attributes are emitted into the assembly and therefore into its REFERENCE "
            + "assembly, which meshweaver-surface.manifest hashes into the framework build "
            + "identity. CD publishes the portal image WITH -p:Version= and the bake/tester image "
            + "WITHOUT it, so any dependence here makes the two images of one commit resolve "
            + "different identities and every published bake INERT (#3022, #1814). Derive the "
            + "compiled attributes from $(PlatformVersion) unconditionally; -p:Version= must move "
            + "$(Version) — the package version, image tag and MESHWEAVER_PLATFORM_VERSION — and "
            + "nothing else.");

        var leaked = CompiledAttributes
            .Where(name => bare[name].Contains(ProbeVersion, StringComparison.Ordinal)
                           || overridden[name].Contains(ProbeVersion, StringComparison.Ordinal))
            .ToList();

        Assert.True(leaked.Count == 0,
            $"The supplied version string reached a compiled attribute ({string.Join(", ", leaked)}). "
            + "Same defect as above; see #3022.");
    }

    /// <summary>
    /// 🚨 The control arm. Without it the test above passes whenever the <c>-p:</c> never reached
    /// MSBuild at all — a mistyped switch, a renamed project, an evaluation that silently failed —
    /// which is a guard measuring nothing, dressed as a green tick. <c>$(Version)</c> is the ONE
    /// property the override is supposed to move, so it must differ across the two runs, and the
    /// bare run must produce the repo's own scheme rather than an SDK default.
    /// </summary>
    [Fact]
    public void TheOverrideActuallyReachedTheEvaluation()
    {
        var bare = Evaluate()["Version"];
        var overridden = Evaluate($"-p:Version={ProbeVersion}")["Version"];

        Assert.Equal(ProbeVersion, overridden);
        Assert.NotEqual(ProbeVersion, bare);
        // The control arm only has to prove the repo sets a real $(Version) — i.e. that the
        // override actually CHANGED something and the bare evaluation is not the SDK default.
        // 🚨 Deliberately NOT `StartsWith("3.")`: pinning the major would red this guard the day
        // the platform rolls to 4.x, while the invariant it guards still holds perfectly. A guard
        // that fails for a reason unrelated to its subject gets muted, and then the subject is
        // unguarded.
        Assert.NotEqual("1.0.0", bare);
        Assert.False(string.IsNullOrWhiteSpace(bare), "the repo must set a real $(Version)");
    }

    /// <summary>
    /// The probed project's assembly must be part of the canonical content surface — the set whose
    /// per-assembly surface hashes ARE the framework build identity. If it ever leaves that set the
    /// guard would still measure a real MSBuild evaluation, but no longer one that can fork a bake
    /// address, so it must be re-pointed rather than quietly kept.
    /// </summary>
    [Fact]
    public void ProbedAssemblyIsPartOfTheContentSurface()
        => Assert.Contains(ProbeAssembly, FrameworkBuildIdentity.ContentSurfaceAssemblies);


    /// <summary>
    /// Evaluates <see cref="ProbeProject"/> and returns the compiled attributes plus
    /// <c>$(Version)</c>. The launcher itself lives in <see cref="MsBuildPropertyProbe"/>, shared
    /// with <see cref="CdImageLegPropertiesDoNotForkTheIdentityGuard"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
        => MsBuildPropertyProbe.Evaluate(ProbeProject, [.. CompiledAttributes, "Version"], extraArguments);
}
